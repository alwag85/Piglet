using System;
using System.Collections.Generic;
using System.Linq;

namespace Piglet.Lexer.Construction
{
    internal class DFA : FiniteAutomata<DFA.State>
    {
        public class State : BaseState
        {
            public ISet<NFA.State> NfaStates { get; private set; }
            public bool Mark { get; set; }

            public State(ISet<NFA.State> nfaStates)
            {
                NfaStates = nfaStates;
            }

			public IEnumerable<CharRange> LegalMoves(Transition<NFA.State>[] fromTransitions)
            {
            	return fromTransitions.SelectMany(f => f.ValidInput.Ranges).Distinct();
            }

            public override string ToString()
            {
                // Purely for debugging purposes
                return string.Format( "{0} {{{1}}}", StateNumber, String.Join( ", ", NfaStates));
            }

        	public override bool AcceptState
            {
                get { return NfaStates.Any(f=>f.AcceptState); }
                set {}  // Do nothing, cannot set
            }
        }

        public static DFA Create(NFA nfa)
        {
            var closures = nfa.GetAllClosures();

            // The valid input ranges that the NFA contains will need to be split up so that
            // the smallest possible units which NEVER overlaps will be contained in each of the
            // states
            nfa.DistinguishValidInputs();

            // Get the closure set of S0
            var dfa = new DFA();
            dfa.States.Add(new State(closures[nfa.StartState]));
            
            while (true)
            {
                // Get an unmarked state in dfaStates
                var t = dfa.States.FirstOrDefault(f => !f.Mark);
                if (null == t)
                {
                    // We're done!
                    break;
                }

                t.Mark = true;

                // Get the move states by stimulating this DFA state with
                // all possible characters.
				var fromTransitions = nfa.Transitions.Where(f => t.NfaStates.Contains(f.From)).ToArray();

            	var moveDestinations = new Dictionary<CharRange, List<NFA.State>>();
            	foreach (var fromTransition in fromTransitions)
            	{
            		foreach (var range in fromTransition.ValidInput.Ranges)
            		{
            			List<NFA.State> destList;
            			if (!moveDestinations.TryGetValue(range, out destList))
            			{
            				destList = new List<NFA.State>();
            				moveDestinations.Add(range, destList);
            			}

						destList.Add(fromTransition.To);
            		}
            	}

                foreach (CharRange c in t.LegalMoves(fromTransitions))
                {
                	var moveSet = moveDestinations[c];
                    if (moveSet.Any())
                    {
                        // Get the closure of the move set. This is the NFA states that will form the new set
                        ISet<NFA.State> moveClosure = new HashSet<NFA.State>();

                        foreach (var moveState in moveSet)
                        {
                            moveClosure.UnionWith(closures[moveState]);
                        }

                        var newState = new State(moveClosure);

                        // See if the new state already exists. If so change the reference to point to 
                        // the already created object, since we will need to add a transition back to the same object
                        var oldState = dfa.States.FirstOrDefault(f => f.NfaStates.SetEquals(newState.NfaStates));/* f.NfaStates.Count == newState.NfaStates.Count && 
																	  !f.NfaStates.Except(newState.NfaStates).Any() &&
                                                                      !newState.NfaStates.Except(f.NfaStates).Any());*/
                        if (oldState == null)
                        {
                            dfa.States.Add(newState);
                        } 
                        else
                        {
                            // New state wasn't that new. We already have one exacly like it in the DFA. Set 
                            // netstate to oldstate so that the created transition will be correct (still need to
                            // create a transition)
                            newState = oldState;
                        }

                        // See if there already is a transition. In that case, add our character to the list
                        // of valid values
                        var transition = dfa.Transitions.SingleOrDefault(f => f.From == t && f.To == newState);
                        if (transition == null) 
                        {
                            // No transition has been found. Create a new one.
                            transition = new Transition<State>(t, newState);
                            dfa.Transitions.Add(transition);                     
                        }

                        transition.ValidInput.AddRange(c.From, c.To, false);
                    }
                }
            }

            dfa.StartState = dfa.States[0];
            dfa.AssignStateNumbers();

            return dfa;
        }

        public void Minimize()
        {
            var distinct = new TriangularTable<int, State>(States.Count, f => f.StateNumber );
            distinct.Fill(-1); // Fill with empty states
            
            // Create a function for the distinct state pairs and performing an action on them
            Action<Action<State, State>> distinctStatePairs = action =>
            {
                for (int i = 0; i < States.Count; ++i)
                {
                    var p = States[i];
                    for (int j = i + 1; j < States.Count; ++j)
                    {
                        var q = States[j];
                        action(p, q);
                    }
                }        
            };

            // Get a set of all valid input ranges that we have in the DFA
            ISet<CharRange> allValidInputs = new HashSet<CharRange>();
            foreach (var transition in Transitions)
            {
                allValidInputs.UnionWith(transition.ValidInput.Ranges);
            }

            // For every distinct pair of states, if one of them is an accepting state
            // and the other one is not set the distinct 
            distinctStatePairs((p, q) =>
            {
                var pIsAcceptState = p.AcceptState;
                var bIsAcceptState = q.AcceptState;
                if (bIsAcceptState && pIsAcceptState)
                {
                    // If both are accepting states, then we might have an issue merging them.
                    // this is because we use multiple regular expressions with different endings when
                    // constructing lexers.
                    var pAcceptStates = p.NfaStates.Where(f => f.AcceptState).ToList();
                    var qAcceptStates = q.NfaStates.Where(f => f.AcceptState).ToList();

                    if (pAcceptStates.Count() == qAcceptStates.Count())
                    {
                        foreach (var pAcceptState in pAcceptStates)
                        {
                            if (!qAcceptStates.Contains(pAcceptState))
                            {
                                // Since the accepting states differ, its not cool to merge
                                // these two states.
                                distinct[p, q] = int.MaxValue;
                            }
                        }
                    }
                    else
                    {
                        // Not the same number of states, not cool to merge
                        distinct[p, q] = int.MaxValue;
                    }
                }

                if (pIsAcceptState ^ bIsAcceptState)
                {
                    distinct[p, q] = int.MaxValue;
                }
            });

			// Make a dictionary of from transitions. This is well worth the time, since
			// this gets accessed lots of times.
        	var targetDict = new Dictionary<State, Dictionary<CharRange, State>>();
        	foreach (var transition in Transitions)
        	{
        		Dictionary<CharRange, State> toDict;
        		targetDict.TryGetValue(transition.From, out toDict);
        		if (toDict == null)
        		{
        			toDict = new Dictionary<CharRange, State>();
        			targetDict.Add(transition.From, toDict);
        		}
        		foreach (var range in transition.ValidInput.Ranges)
        		{
        			toDict.Add(range, transition.To);	
        		}
			}

            // Start iterating
            bool changes;
            do
            {
                changes = false;

                distinctStatePairs((p, q) =>
                {
                    if (distinct[p, q] == -1) 
                    {
                        Func<State, CharRange, State> targetState = (state, c) =>
                        	{
                        		Dictionary<CharRange, State> charDict;
								if (targetDict.TryGetValue(state, out charDict))
								{
									State toState;
									if (charDict.TryGetValue(c, out toState))
									{
										return toState;	
									}
								}
                        		return null;
                        	};

                        foreach (var a in allValidInputs)
                        {
                            var qa = targetState(q, a);
                            var pa = targetState(p, a);

                            if (pa == null ^ qa == null)
                            {
                                // If one of them has a transition on this character range but the other one doesn't then
                                // they are separate.
                                distinct[p, q] = a.GetHashCode();
                                changes = true;

                                break;
                            }
                            
                            // If both are null, then we carry on.
                            // The other one is null implictly since we have XOR checked it earlier
                            if (qa == null) continue;

                            if (distinct[qa, pa] != -1)
                            {
                                distinct[p, q] = a.GetHashCode();
                                changes = true;
                                break;
                            }
                        }                           
                    }
                });
            } while (changes);

            // Merge states that still have blank square
            // To make this work we have to bunch states together since the indices will be screwed up
            var mergeSets = new List<ISet<State>>();
            Func<State, ISet<State>> findMergeList = s => mergeSets.FirstOrDefault(m => m.Contains(s));

            distinctStatePairs((p, q) =>
            {
                // No need to check those that we have already determined to be distinct
                if (distinct[p, q] != -1) return;

                // These two states are supposed to merge!
                // See if p or q is already part of a merge list!
                var pMergeSet = findMergeList(p);
                var qMergeSet = findMergeList(q);

                if (pMergeSet == null && qMergeSet == null)
                {
                    // No previous set for either
                    // Add a new merge set
                    mergeSets.Add(new HashSet<State> { p, q });
                }
                else if (pMergeSet != null && qMergeSet == null)
                {
                    // Add q to pMergeSet
                    pMergeSet.Add(q);
                }
                else if (pMergeSet == null)
                {
                    // Add p to qMergeSet
                    qMergeSet.Add(p);
                }
                else
                {
                    // Both previously have merge sets
                    // If its not the same set (which it shoudln't be) then add their union
                    if (pMergeSet != qMergeSet)
                    {
                        // Union everything into the pMergeSet
                        pMergeSet.UnionWith(qMergeSet);
                            
                        // Remove the qMergeSet
                        mergeSets.Remove(qMergeSet);
                    }
                }
            });

            // Armed with the merge sets, we can now do the actual merge
            foreach (var mergeSet in mergeSets)
            {
                // The lone state that should remain is the FIRST set in the mergeset
                var stateList = mergeSet.ToList();
                var outputState = stateList[0];

                // If this statelist contains the startstate, the new startstate will have to be
                // the new output state
                if (stateList.Contains(StartState))
                {
                    StartState = outputState;
                }

                // Iterate over all the states in the merge list except for the one we have decided
                // to merge everything into.
                for (int i = 1; i < stateList.Count; ++i)
                {
                    var toRemove = stateList[i];

                    // Find all transitions that went to this state
                    var toTransitions = Transitions.Where(f => f.To == toRemove).ToList();
                    foreach (var transition in toTransitions)
                    {
                        // There can be two cases here, either there already is a new transition to be found, in
                        // which case we can merge the valid input instead. The alternative is that there is no prior
                        // transition, in which case we repoint our transition to the output state.
                        var existingTransition = Transitions.FirstOrDefault(f => f.From == transition.From && f.To == outputState);
                        if (existingTransition != null)
                        {
                            existingTransition.ValidInput.UnionWith(transition.ValidInput);
                            Transitions.Remove(transition); // Remove the old transition
                        }
                        else
                        {
                            transition.To = outputState;
                        }
                    }

                    // Find all transitions that went from this state
                    var fromTransitions = Transitions.Where(f => f.From == toRemove).ToList();
                    foreach (var transition in fromTransitions)
                    {
                        // Same two cases as the code above
                        var existingTransition = Transitions.FirstOrDefault(f => f.From == outputState && f.To == transition.To);
                        if (existingTransition != null)
                        {
                            existingTransition.ValidInput.UnionWith(transition.ValidInput);
                            Transitions.Remove(transition); // Remove the old transition
                        }
                        else
                        {
                            transition.From = outputState;
                        }
                    }

                    // Since before removing this state, we need to merge the list of NFA states that created both of these states
                    foreach (var nfaState in toRemove.NfaStates)
                    {
                        if (!outputState.NfaStates.Contains(nfaState))
                        {
                            outputState.NfaStates.Add(nfaState);
                        }
                    }

                    // There should be no more references to this state. It can thus be removed.
                    States.Remove(toRemove);
                }
            }

            // The states now need to be renumbered
            AssignStateNumbers();
        }

        public override IEnumerable<State> Closure(State[] states, ISet<State> visitedStates = null)
        {
            return states;
        }
    }
}