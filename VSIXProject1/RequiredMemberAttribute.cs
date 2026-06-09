using System;

namespace System.Runtime.CompilerServices
{
    // Minimal shim for RequiredMemberAttribute so projects targeting older frameworks
    // (or without the attribute available) can compile and run code using 'required'.
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Parameter | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class RequiredMemberAttribute : Attribute
    {
        public RequiredMemberAttribute()
        {
        }
    }

    // Shim for C# init-only setters support
    // The compiler looks for this type to emit init accessors when targeting older frameworks.
    public static class IsExternalInit { }

    // Shim for compiler feature gating attribute (used by required and other features).
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Event | AttributeTargets.Parameter, Inherited = false)]
    public sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string feature)
        {
        }
    }
}
