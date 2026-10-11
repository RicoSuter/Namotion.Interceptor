using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

[InterceptorSubject]
[Category("Devices")]
[Description("Counts stripes")]
[DisplayName("Zebra sensor")]
[SubjectIcon("Pets")]
public partial class CatalogAnnotatedSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogAnnotatedSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[Category("Devices")]
[DisplayName("Alpha relay")]
public partial class CatalogSecondDeviceSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogSecondDeviceSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[Category("Servers")]
public partial class CatalogServerSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogServerSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class CatalogPlainSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogPlainSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[ExcludeFromCreation]
public partial class CatalogExcludedSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogExcludedSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[SingleInstance]
public partial class CatalogSingleInstanceSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogSingleInstanceSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[Category("devices")]
[DisplayName("Aardvark feeder")]
public partial class CatalogLowerCaseGroupSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogLowerCaseGroupSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public abstract partial class CatalogAbstractSubject : IConfigurable
{
    public partial string? Label { get; set; }

    protected CatalogAbstractSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class CatalogNotConfigurableSubject
{
    public partial string? Label { get; set; }

    public CatalogNotConfigurableSubject()
    {
    }
}

[InterceptorSubject]
public partial class CatalogDerivedAnnotatedSubject : CatalogAnnotatedSubject
{
    public CatalogDerivedAnnotatedSubject()
    {
    }
}

[InterceptorSubject]
public partial class CatalogDerivedExcludedSubject : CatalogExcludedSubject
{
    public CatalogDerivedExcludedSubject()
    {
    }
}

[InterceptorSubject]
public partial class CatalogDerivedSingleInstanceSubject : CatalogSingleInstanceSubject
{
    public CatalogDerivedSingleInstanceSubject()
    {
    }
}

[Category("")]
[Description("")]
[DisplayName("")]
[SubjectIcon("")]
public class CatalogEmptyAttributesSubject;

[Category(" ")]
[Description("\t")]
[DisplayName("  ")]
[SubjectIcon(" ")]
public class CatalogBlankAttributesSubject;
