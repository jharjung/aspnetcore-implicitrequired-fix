using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace JoeHarjung.AspNetCore.ImplicitRequiredFix
{
    public class PathAwareObjectModelValidator : ObjectModelValidator
    {
        private readonly MvcOptions _MvcOptions;

        public PathAwareObjectModelValidator(IModelMetadataProvider ModelMetadataProvider, MvcOptions MvcOptions)
            : base(ModelMetadataProvider, MvcOptions.ModelValidatorProviders)
        {
            _MvcOptions = MvcOptions;
        }

        public override ValidationVisitor GetValidationVisitor(ActionContext ActionContext, IModelValidatorProvider ValidatorProvider, ValidatorCache ValidatorCache, IModelMetadataProvider MetadataProvider, ValidationStateDictionary ValidationState) =>
            new PathAwareValidationVisitor(ActionContext, ValidatorProvider, ValidatorCache, MetadataProvider, ValidationState)
            {
                MaxValidationDepth = _MvcOptions.MaxValidationDepth,
                ValidateComplexTypesIfChildValidationFails = _MvcOptions.ValidateComplexTypesIfChildValidationFails
            };
    }
}
