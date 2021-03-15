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
    public class PathAwareValidationVisitor : ValidationVisitor
    {
        private DefaultModelMetadata? _Root = null;

        public PathAwareValidationVisitor(ActionContext ActionContext, IModelValidatorProvider ValidatorProvider, ValidatorCache ValidatorCache, IModelMetadataProvider MetadataProvider, ValidationStateDictionary? ValidationState) 
            : base(ActionContext, ValidatorProvider, ValidatorCache, MetadataProvider, ValidationState)
        {
        }

        public override bool Validate(ModelMetadata Metadata, string Key, object Model, bool AlwaysValidateAtTopLevel, object Container)
        {
            if (Metadata is DefaultModelMetadata m)
                _Root = m;
            else
                throw new InvalidOperationException("Wrong metadata type");

            try
            {
                return base.Validate(Metadata, Key, Model, AlwaysValidateAtTopLevel, Container);
            }
            finally
            {
                _Root = null;
            }
        }

        protected override bool ValidateNode()
        {
            if (this.Metadata is not DefaultModelMetadata)
                throw new InvalidOperationException("Wrong metadata type");

            if (_Root == null)
                throw new InvalidOperationException("No root");

            if (_Root.MetadataKind != ModelMetadataKind.Parameter)
                throw new InvalidOperationException("Root is not a parameter");

            var metadata = (DefaultModelMetadata)this.Metadata;
            var state = this.ModelState.GetValidationState(this.Key);

            if (state != ModelValidationState.Invalid)
            {
                var validators = this.Cache.GetValidators(this.Metadata, this.ValidatorProvider);
                var count = validators.Count;

                if (count > 0)
                {
                    var context = new PathAwareModelValidationContext
                    (
                        _Root,
                        this.Key,
                        this.Context,
                        metadata,
                        this.MetadataProvider,
                        this.Container,
                        this.Model
                    );

                    var results = new List<ModelValidationResult>();
                    for (var i = 0; i < count; i++)
                        results.AddRange(validators[i].Validate(context));

                    var resultsCount = results.Count;
                    for (var i = 0; i < resultsCount; i++)
                    {
                        var result = results[i];
                        var key = ModelNames.CreatePropertyModelName(this.Key, result.MemberName);

                        this.ModelState.TryAddModelError(key, result.Message);
                    }
                }
            }

            state = this.ModelState.GetFieldValidationState(this.Key);

            if (state == ModelValidationState.Invalid)
            {
                return false;
            }
            else
            {
                var entry = this.ModelState[this.Key];

                if (entry != null)
                    entry.ValidationState = ModelValidationState.Valid;

                return true;
            }
        }
    }
}
