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
    public class PathAwareModelValidationContext : ModelValidationContext
    {
        public DefaultModelMetadata RootModelMetadata { get; }
        public string Path { get; }

        public PathAwareModelValidationContext
        (
            DefaultModelMetadata RootModelMetadata,
            string Path,
            ActionContext ActionContext,
            DefaultModelMetadata ModelMetadata,
            IModelMetadataProvider MetadataProvider,
            object Container,
            object Model
        )
            : base(ActionContext, ModelMetadata, MetadataProvider, Container, Model)
        {
            this.RootModelMetadata = RootModelMetadata;
            this.Path = Path;
        }
    }
}
