using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.Options;

namespace Chat.Api.Files;

/// <summary>Marks an upload action for <see cref="UploadLimitsSetup"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadLimitsAttribute : Attribute;

/// <summary>
/// The API reads the whole file to check it, so an upload's request limit is the file limit plus room for the multipart framing.
/// The file limit comes from configuration, so a convention adds the limits instead of an attribute.
/// </summary>
public sealed class UploadLimitsSetup(IOptions<MediaOptions> media) : IConfigureOptions<MvcOptions>
{
    /// <summary>Room for the multipart boundaries and part headers around the file.</summary>
    public const long MultipartAllowance = 64 * 1024;

    public void Configure(MvcOptions options) => options.Conventions.Add(new Convention(media.Value.MaxBytes + MultipartAllowance));

    private sealed class Convention(long maxRequest) : IActionModelConvention
    {
        public void Apply(ActionModel action)
        {
            if (!action.Attributes.OfType<UploadLimitsAttribute>().Any()) return;
            action.Filters.Add(new RequestSizeLimitAttribute(maxRequest));
            action.Filters.Add(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxRequest });
        }
    }
}
