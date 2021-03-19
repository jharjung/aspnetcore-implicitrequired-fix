using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace JoeHarjung.AspNetCore.ImplicitRequiredFix
{
    public record NullabilityTree(byte Flag, ImmutableDictionary<Type, NullabilityTree> TypeArgs)
    {
        public NullabilityTree(byte Flag) : this(Flag, ImmutableDictionary<Type, NullabilityTree>.Empty) { }

        public NullabilityTree Apply(ImmutableDictionary<Type, byte> Outer)
        {
            var args = this.TypeArgs
                .Select(kvp =>
                {
                    if (Outer.TryGetValue(kvp.Key, out var flag))
                    {
                        if (kvp.Value.TypeArgs.Any())
                            throw new InvalidOperationException("Unconstructed generic type arg shouldn't have inner type args");

                        return (kvp.Key, Value: new NullabilityTree(flag));
                    }
                    else
                    {
                        return (kvp.Key, Value: kvp.Value.Apply(Outer));
                    }
                })
                .ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return new NullabilityTree(this.Flag, args);
        }

        public static NullabilityTree UnpackFlags(Type PropertyType, IEnumerable<object> PropertyAttributes, IEnumerable<object> ClassAttributes)
        {
            Stack<byte>? flags = null;
            byte? contextFlag;

            if (TryGetNullableFlags(PropertyAttributes, out var propFlags))
            {
                ConstructFlags(propFlags, out flags, out contextFlag);
            }
            else
            {
                _ = TryGetNullableContextFlag(ClassAttributes, out contextFlag);
            }

            return Traverse(PropertyType, flags, contextFlag);
        }

        public static NullabilityTree UnpackFlags(Type ParameterType, IEnumerable<object> ParameterAttributes, IEnumerable<object> MethodAttributes, IEnumerable<object> ClassAttributes)
        {
            Stack<byte>? flags = null;
            byte? contextFlag;

            if (TryGetNullableFlags(ParameterAttributes, out var propFlags))
            {
                ConstructFlags(propFlags, out flags, out contextFlag);
            }
            else
            {
                if (!TryGetNullableContextFlag(MethodAttributes, out contextFlag))
                    _ = TryGetNullableContextFlag(ClassAttributes, out contextFlag);
            }

            return Traverse(ParameterType, flags, contextFlag);
        }

        public static byte GetTypeDefParameterFlag(Type TypeDefParameter)
        {
            if (!TypeDefParameter.IsGenericTypeParameter || TypeDefParameter.DeclaringType == null)
                throw new ArgumentException("Argument is not a generic type parameter", nameof(TypeDefParameter));

            if (TryGetNullableFlags(TypeDefParameter.GetCustomAttributes(false), out var paramFlags))
                return paramFlags[0];

            if (TryGetNullableContextFlag(TypeDefParameter.DeclaringType.GetCustomAttributes(false), out var contextFlag))
                return contextFlag.Value;

            return 0;
        }

        private static void ConstructFlags(byte[] Value, out Stack<byte>? Flags, out byte? ContextFlag)
        {
            if (Value.Length == 1)
            {
                Flags = null;
                ContextFlag = Value[0];
            }
            else
            {
                Flags = new Stack<byte>(Value.Reverse());
                ContextFlag = null;
            }
        }

        private static NullabilityTree Traverse(Type ConcreteType, Stack<byte>? Flags, byte? ContextFlag)
        {
            var flag = GetFlag(ConcreteType, Flags, ContextFlag);
            var inner = new Dictionary<Type, NullabilityTree>();

            if (ConcreteType.IsGenericType)
            {
                var concreteArgs = ConcreteType.GetGenericArguments();
                var defArgs = ConcreteType.GetGenericTypeDefinition().GetGenericArguments();

                foreach (var (c, d) in concreteArgs.Zip(defArgs))
                    inner.Add(d, Traverse(c, Flags, ContextFlag));
            }
            else if (ConcreteType.IsArray)
            {
                var elemType = ConcreteType.GetElementType()!;
                inner.Add(elemType, Traverse(elemType, Flags, ContextFlag));
            }

            return new NullabilityTree(flag, inner.ToImmutableDictionary());
        }

        private static byte GetFlag(Type ConcreteType, Stack<byte>? Flags, byte? ContextFlag)
        {
            byte flag;

            if (ConcreteType.IsValueType)
            {
                flag = Nullable.GetUnderlyingType(ConcreteType) == null ? (byte)1 : (byte)2;
            }
            else
            {
                if (Flags == null)
                    flag = ContextFlag ?? 0;
                else
                    flag = Flags.Pop();
            }

            return flag;
        }

        private static bool TryGetNullableFlags(IEnumerable<object> PropertyOrParameterAttributes, [NotNullWhen(true)] out byte[]? Flags)
        {
            if (TryGetAttribute(PropertyOrParameterAttributes, "System.Runtime.CompilerServices.NullableAttribute", out var attr))
            {
                var field = attr.GetType().GetField("NullableFlags")!;

                Flags = (byte[])field.GetValue(attr)!;
                return true;
            }

            Flags = null;
            return false;
        }

        private static bool TryGetNullableContextFlag(IEnumerable<object> ClassOrMethodAttributes, [NotNullWhen(true)] out byte? Flag)
        {
            if (TryGetAttribute(ClassOrMethodAttributes, "System.Runtime.CompilerServices.NullableContextAttribute", out var attr))
            {
                var field = attr.GetType().GetField("Flag")!;

                Flag = (byte)field.GetValue(attr)!;
                return true;
            }

            Flag = null;
            return false;
        }

        private static bool TryGetAttribute(IEnumerable<object> Attributes, string Name, [NotNullWhen(true)] out object? Attribute)
        {
            var attr = Attributes.FirstOrDefault(a => a.GetType().FullName == Name);

            if (attr == null)
            {
                Attribute = null;
                return false;
            }

            Attribute = attr;
            return true;
        }
    }
}
