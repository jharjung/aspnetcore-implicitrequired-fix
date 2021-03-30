# aspnetcore-implicitrequired-fix
Asp.Net Core's ImplicitRequiredAttributeForNonNullableReferenceTypes is known to be broken for generic types.
See: https://github.com/dotnet/aspnetcore/issues/21501

This project implements a `NullabilityModelValidator` to provide proper validation of null values for (non-)nullable reference types in projects that have opted in to C# 8's nullability analysis.

Resolving nullability at the metadata provider level, as `DataAnnotationsMetadataProvider` attempts to do, is a dead end. The metadata is computed and cached for a runtime `System.Type`. Erasure of reference type nullability means that `List<string?>?` and `List<string>` are the same type at runtime, and therefore have the same metadata. Not only the "same" metadata, but the same metadata *instance*. Given this, it's not possible to represent reference type nullability in the validation metadata without either: 
- pushing it into the `ModelMetadataIdentity`, thus creating different metadata instances, but fundamentally changing how metadata is created and retrieved
  - *or* 
- tracking it for *every* possible place this type could be used

To properly resolve nullability, we need:
- The metadata for the root model (the top-level type of the action parameter)
- The full path from the root to the value being validated (eg, `MyActionParam.MyList[3].MyProp.MyDict[12]...`)

This information isn't available on `ModelValidationContext`, but it's easily obtainable within `ValidationVisitor`, which constructs the `ModelValidationContext` and invokes the `IModelValidator`. Exposing this information to an `IModelValidator` is a simple matter of subclassing `ModelValidationContext` to add the properties, subclassing `ValidationVisitor` to pass them in to the new validation context's constructor, and subclassing `ObjectModelValidator` to construct the new validation visitor.

With this information in hand, an `IModelValidator` can walk down the metadata tree, traversing through the Property and Element metadata, and reconstruct the nullability of generic types from the `[Nullable]` attributes found at their parameter/property declaration sites (and of course the `[NullableContext]` attributes of the declaring type, where applicable).

Please be aware that this project is a proof of concept, and while it works for my use cases, type systems are complex things with big surface areas. I definitely haven't considered every possible crazy thing you could legally do with generic types in C#, and I've likely overlooked a couple not so crazy things too.

To get going, wire everything up in ConfigureServices like this:

```
var mvcCore = Services.AddMvcCore();

// A DefaultObjectValidator will be registered by Services.AddMvc() or Services.AddMvcCore()
// Remove it and replace it with our own

var omv = Services.Single(d => d.ServiceType == typeof(IObjectModelValidator));
Services.Remove(omv);

Services.AddSingleton<IObjectModelValidator>(s =>
{
  var opts = s.GetRequiredService<IOptions<MvcOptions>>().Value;
  return new PathAwareObjectModelValidator(s.GetRequiredService<IModelMetadataProvider>(), opts);
});

mvcCore.AddMvcOptions(opts =>
{
  // Don't forget to disable the built-in validation, so it doesn't flag
  // false positives (valid nulls for nullable reference types) on
  // properties of generic classes and elements of generic collections
  opts.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
  
  // Inject the nullability validator
  opts.ModelMetadataDetailsProviders.Insert(0, new NullabilityValidationMetadataProvider());
  opts.ModelValidatorProviders.Insert(0, new NullabilityModelValidatorProvider());
});
```
