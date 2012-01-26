namespace Piglet.Configuration
{
    public static class ParserConfiguratorFactory
    {
        public static IParserConfigurator<T>  CreateConfigurator<T>()
        {
            return new ParserConfigurator<T>();
        }
    }
}