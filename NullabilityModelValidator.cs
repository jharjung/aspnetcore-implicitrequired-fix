using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace JoeHarjung.AspNetCore.ImplicitRequiredFix
{
    public class NullabilityModelValidatorProvider : IModelValidatorProvider
    {
        private static readonly NullabilityModelValidator _Validator = new NullabilityModelValidator();

        public void CreateValidators(ModelValidatorProviderContext Context)
        {
            Context.Results.Add(new ValidatorItem
            {
                Validator = _Validator,
                IsReusable = true
            });
        }
    }

    public class NullabilityModelValidator : IModelValidator
    {
        private static readonly ConcurrentDictionary<(MethodInfo Action, string Parameter, string Path), byte> _Nullability = new ConcurrentDictionary<(MethodInfo Action, string Parameter, string Path), byte>();
        private static readonly Regex _PathPattern = new Regex(@"^((?<ident>(\[\d+\])|([^.[]+))(\.|(?=$|\[)))*$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private const string _PathIndex = "[_]";

        public IEnumerable<ModelValidationResult> Validate(ModelValidationContext Context)
        {
            if (Context is PathAwareModelValidationContext ctx &&
                Context.ActionContext is ControllerContext actionCtx)
            {
                if (ctx.Model == null && actionCtx.ModelState.ErrorCount == 0)
                {
                    var key = (actionCtx.ActionDescriptor.MethodInfo, ctx.RootModelMetadata.ParameterName!, ctx.Path);

                    var flag = _Nullability.GetOrAdd(key, key =>
                    {
                        var path = ParseModelPath(ctx.Path);
                        var pathMeta = GetPathModelMetadata(ctx.RootModelMetadata, path);

                        return ResolveNullability(actionCtx, pathMeta);
                    });

                    if (flag == 1)
                        yield return new ModelValidationResult("", $"The {ctx.ModelMetadata.GetDisplayName()} field is required.");
                }
            }
            else
            {
                throw new InvalidOperationException("Wrong context type");
            }
        }

        private static IEnumerable<string> ParseModelPath(string Path)
        {
            var match = _PathPattern.Match(Path);

            if (!match.Success)
                throw new ArgumentException("Path is invalid", nameof(Path));

            return match.Groups[1].Captures
                .Select(c => c.Value[0] == '[' ? _PathIndex : c.Value);
        }

        private static IEnumerable<DefaultModelMetadata> GetPathModelMetadata(DefaultModelMetadata Current, IEnumerable<string> Path)
        {
            yield return Current;

            if (Current.MetadataKind == ModelMetadataKind.Parameter &&
                Current.BindingSource != BindingSource.Body)
            {
                Path = Path.Skip(1);
            }

            foreach (var part in Path)
            {
                if (part == _PathIndex)
                {
                    if (Current.ElementMetadata == null)
                        throw new InvalidOperationException($"{Current.ModelType.Name} is not a collection");

                    if (Current.ElementMetadata is not DefaultModelMetadata)
                        throw new InvalidOperationException("ModelMetdata is not a DefaultModelMetadata");

                    Current = (DefaultModelMetadata)Current.ElementMetadata;
                    yield return Current;
                }
                else
                {
                    var prop = Current.Properties.SingleOrDefault(p => p.PropertyName == part);

                    if (prop == null)
                        throw new InvalidOperationException($"{Current.ModelType.Name} has no property {part}");

                    if (prop is not DefaultModelMetadata)
                        throw new InvalidOperationException("ModelMetdata is not a DefaultModelMetadata");

                    Current = (DefaultModelMetadata)prop;
                    yield return Current;
                }
            }
        }

        private static byte ResolveNullability(ControllerContext Action, IEnumerable<DefaultModelMetadata> PathModelMetadata)
        {
            DefaultModelMetadata? prev = null;
            NullabilityTree? current = null;

            foreach (var meta in PathModelMetadata)
            {
                if (meta.MetadataKind == ModelMetadataKind.Parameter)
                {
                    if (current == null)
                    {
                        var actionMethod = Action.ActionDescriptor.MethodInfo;
                        var methodAttrs = actionMethod.GetCustomAttributes(false);
                        var classAttrs = actionMethod.DeclaringType!.GetCustomAttributes(false);

                        current = NullabilityTree.UnpackFlags(meta.ModelType, meta.Attributes.ParameterAttributes, methodAttrs, classAttrs);
                    }
                    else
                    {
                        throw new InvalidOperationException("Metadata of kind Parameter should be top level, pertaining to the action method parameter");
                    }
                }
                else if (meta.MetadataKind == ModelMetadataKind.Type)
                {
                    if (current == null || prev == null || prev.ElementMetadata != meta)
                        throw new InvalidOperationException("Metadata of kind Type that is not the root should be the element type of a collection");

                    current = GetCollectionElementNullability(current, prev);
                }
                else if (meta.MetadataKind == ModelMetadataKind.Property)
                {
                    if (current == null || prev == null || meta.ContainerMetadata == null)
                        throw new InvalidOperationException("Property is not contained in a type");

                    current = GetPropertyNullability(current, prev, meta);
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected validation metadata of kind {meta.MetadataKind}");
                }

                prev = meta;
            }

            if (current == null)
                throw new InvalidOperationException("No path model metadata");

            return current.Flag;
        }

        private static NullabilityTree GetCollectionElementNullability(NullabilityTree CollectionNullability, ModelMetadata CollectionMetadata)
        {
            var isGeneric = CollectionMetadata.ModelType.IsGenericType;
            var typeDef = isGeneric ? CollectionMetadata.ModelType.GetGenericTypeDefinition() : null;
            var typeDefArgs = typeDef == null ? Array.Empty<Type>() : typeDef.GetGenericArguments();

            if (typeof(IDictionary).IsAssignableFrom(CollectionMetadata.ModelType))
            {
                if (CollectionNullability.TypeArgs.Count != 2 || typeDefArgs.Length != 2)
                    throw new InvalidOperationException("Could not correlate KeyValuePair element type to Key and Value generic type parameters");

                if (!CollectionMetadata.ElementType!.IsGenericType || CollectionMetadata.ElementType!.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                    throw new InvalidOperationException("Could not correlate KeyValuePair element type to Key and Value generic type parameters");

                var kvpNullability = typeDefArgs
                    .Zip(typeof(KeyValuePair<,>).GetGenericArguments(), (dictArg, kvpArg) => (dictArg, kvpArg))
                    .ToImmutableDictionary
                    (
                        z => z.kvpArg,
                        z => CollectionNullability.TypeArgs[z.dictArg]
                    );

                return new NullabilityTree(1, kvpNullability);
            }
            else if (typeof(IEnumerable).IsAssignableFrom(CollectionMetadata.ModelType))
            {
                if (CollectionNullability.TypeArgs.Count != 1 || (!CollectionMetadata.ModelType.IsArray && typeDefArgs.Length != 1))
                    throw new InvalidOperationException("Could not correlate collection element type to a generic type parameter");

                return CollectionNullability.TypeArgs.Single().Value;
            }
            else
            {
                throw new NotSupportedException($"Unsupported collection type {CollectionMetadata.ModelType.Name}");
            }
        }

        private static NullabilityTree GetPropertyNullability(NullabilityTree ContainerNullability, DefaultModelMetadata ContainerMetadata, DefaultModelMetadata PropertyMetadata)
        {
            if (PropertyMetadata.ContainerType == null)
                throw new InvalidOperationException("No container type");

            if (PropertyMetadata.PropertyName == null)
                throw new InvalidOperationException("No property name");

            var propInfo = PropertyMetadata.ContainerType.GetProperty(PropertyMetadata.PropertyName, BindingFlags.Public | BindingFlags.Instance);
            var declaringType = propInfo?.DeclaringType ?? PropertyMetadata.ContainerType;
            var declaringClassAttrs = declaringType == PropertyMetadata.ContainerType ? ContainerMetadata.Attributes.TypeAttributes : declaringType.GetCustomAttributes(false);

            if (PropertyMetadata.ContainerType.IsGenericType)
            {
                var typeDef = PropertyMetadata.ContainerType.GetGenericTypeDefinition();
                var typeDefProp = typeDef.GetProperty(PropertyMetadata.PropertyName);

                if (typeDefProp == null)
                    throw new InvalidOperationException($"No such property {PropertyMetadata.PropertyName} in generic type def");

                if (typeDefProp.PropertyType.IsGenericParameter)
                {
                    return ContainerNullability.TypeArgs[typeDefProp.PropertyType];
                }
                else if (PropertyMetadata.ModelType.IsGenericType)
                {
                    var classArgsNullability = typeDef
                        .GetGenericArguments()
                        .ToImmutableDictionary
                        (
                            arg => arg,
                            arg => ContainerNullability.TypeArgs[arg].Flag
                        );

                    return NullabilityTree
                        .UnpackFlags(PropertyMetadata.ModelType, PropertyMetadata.Attributes.PropertyAttributes, declaringClassAttrs)
                        .Apply(classArgsNullability);
                }
            }

            return NullabilityTree
                .UnpackFlags(PropertyMetadata.ModelType, PropertyMetadata.Attributes.PropertyAttributes, declaringClassAttrs);
        }
    }
}
