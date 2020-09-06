namespace Serilog.Expressions
{
    static class BuiltInProperty
    {
        public const string Exception = "x";
        public const string Level = "l";
        public const string Timestamp = "t";
        public const string Message = "m";
        public const string MessageTemplate = "mt";
        public const string Properties = "p";
        
        // Undocumented, simplifies a few scenarios; an `undefined()` may be better.
        public const string Undefined = "Undefined";
    }
}