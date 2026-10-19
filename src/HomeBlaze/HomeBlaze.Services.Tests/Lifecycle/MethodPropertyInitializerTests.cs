using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Lifecycle;

public class MethodPropertyInitializerTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
    }

    [Fact]
    public void WhenAMethodHasAnOperationAttribute_ThenAMethodMetadataPropertyIsRegistered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
        Assert.Equal("Stop", metadata.Title);
        Assert.True(metadata.RequiresConfirmation);
    }

    [Fact]
    public void WhenAMethodHasAQueryAttribute_ThenAMethodMetadataPropertyIsRegistered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("GetDiagnostics");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Query, metadata.Kind);
        Assert.Equal("Diagnostics", metadata.Title);
        Assert.Equal("diag-icon", metadata.Icon);
        Assert.Equal(2, metadata.Position);
    }

    [Fact]
    public void WhenAnOperationHasParameters_ThenItsMetadataDescribesThem()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        var userParameters = metadata.Parameters.Where(p => !p.IsFromServices).ToArray();
        Assert.Single(userParameters);
        Assert.Equal("temperature", userParameters[0].Name);
        Assert.Equal(typeof(decimal), userParameters[0].Type);
    }

    [Fact]
    public async Task WhenAnOperationIsInvoked_ThenTheUnderlyingMethodIsCalled()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task WhenAnAsyncMethodIsInvoked_ThenItsResultIsReturned()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("GetDiagnostics");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Equal("diagnostics-result", result);
        Assert.Equal(typeof(string), metadata.ResultType);
    }

    [Fact]
    public void WhenTheAttributeSetsATitle_ThenTheMetadataUsesIt()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Stop", metadata!.Title);
    }

    [Fact]
    public void WhenTheAttributeSetsNoTitle_ThenTheMethodNameWithoutAsyncIsUsed()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("SetTarget", metadata!.Title);
    }

    [Fact]
    public void WhenTheAttributeIsOnAnInterfaceMethod_ThenTheMethodIsDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("InterfaceOperation");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
    }

    [Fact]
    public void WhenAParameterIsACancellationToken_ThenItIsRuntimeProvided()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var ctParam = metadata!.Parameters.Single(p => p.Name == "cancellationToken");
        Assert.True(ctParam.IsRuntimeProvided);
        Assert.False(ctParam.RequiresInput);
    }

    [Fact]
    public void WhenAParameterIsFromServices_ThenItIsMarkedFromServicesRatherThanAutoInjected()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsRuntimeProvided);
        Assert.False(svcParam.RequiresInput);
    }

    [Fact]
    public void WhenAFromServicesParameterIsNullable_ThenItIsMarkedNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — ITestLogger? is nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsNullable);
    }

    [Fact]
    public void WhenAFromServicesParameterIsNotNullable_ThenItIsNotMarkedNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithRequiredServiceSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — ITestLogger (non-nullable) is not nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsNullable);
    }

    [Fact]
    public void WhenAParameterIsRegular_ThenItRequiresInput()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var valueParam = metadata!.Parameters.Single(p => p.Name == "value");
        Assert.False(valueParam.IsRuntimeProvided);
        Assert.False(valueParam.IsFromServices);
        Assert.True(valueParam.RequiresInput);
    }

    [Fact]
    public async Task WhenAMethodTakingACancellationTokenIsInvoked_ThenTheTokenIsPassedThrough()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;
        var cts = new CancellationTokenSource();

        // Act — only pass user parameters; CancellationToken is injected via the argument
        await metadata!.InvokeAsync([42], null, cts.Token);

        // Assert
        Assert.Equal(42, subject.LastValue);
        Assert.Equal(cts.Token, subject.LastToken);
    }

    [Fact]
    public void WhenAMethodHasNoParameters_ThenItsParametersAreEmpty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Empty(metadata!.Parameters);
    }

    [Fact]
    public async Task WhenAParameterlessMethodIsInvokedWithNullParameters_ThenItIsCalled()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task WhenAParameterlessMethodIsInvokedWithEmptyParameters_ThenItIsCalled()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync([], null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public void WhenAMethodReturnsVoid_ThenItsResultTypeIsNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Reset");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task WhenAVoidMethodIsInvoked_ThenItReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Reset");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Null(result);
        Assert.True(subject.ResetCalled);
    }

    [Fact]
    public async Task WhenAMethodReturnsANonGenericTask_ThenItsResultTypeIsNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task WhenAnInvokedMethodThrows_ThenTheOriginalExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Fail");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert — TargetInvocationException is unwrapped to surface the original exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Something went wrong", exception.Message);
    }

    [Fact]
    public async Task WhenAnInvokedAsyncMethodThrows_ThenTheOriginalExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("FailAsync");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert — TargetInvocationException is unwrapped to surface the original exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Async failure", exception.Message);
    }

    [Fact]
    public async Task WhenInvokedWithTooFewParameters_ThenItThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
    }

    [Fact]
    public async Task WhenInvokedWithTooManyParameters_ThenItThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync([1, 2], null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
        Assert.Contains("received 2", exception.Message);
    }

    [Fact]
    public void WhenAMethodHasADescription_ThenTheMetadataKeepsIt()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DescribedMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Shutdown");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Shuts down the system safely", metadata!.Description);
    }

    [Fact]
    public void WhenAMethodHasNoAttribute_ThenItIsNotDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — InterfaceOperationAsync exists but "ToString" should not be a method property
        var property = registered.TryGetProperty("ToString");

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public async Task WhenAnInvokedMethodThrowsAfterAnAwait_ThenTheExceptionPropagates()
    {
        // Arrange — tests that exceptions thrown after an await are correctly propagated
        var context = CreateContext();
        var subject = new TrulyAsyncThrowingSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("FailAfterAwait");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("truly async failure", exception.Message);
    }

    [Fact]
    public void WhenOnlyTheInterfaceMethodHasTheAttribute_ThenTheMethodIsRegisteredOnce()
    {
        // Arrange — when both type and interface have the same method name,
        // the concrete type's version is registered (deduplication by name)
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — InterfaceOperationAsync is defined on both the interface and the type.
        // The interface declares [Operation(Title = "Interface Op")] but the concrete
        // implementation has no attribute, so the interface attribute is what gets discovered.
        var property = registered.TryGetProperty("InterfaceOperation");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — only one property registered for this method name
        var allMethods = registered.GetAllMethods();
        Assert.Equal(1, allMethods.Count(m => m.Title == "Interface Op" || m.Title == "InterfaceOperation"));
    }

    [Fact]
    public void WhenASubjectWithAnOperationReEntersTheGraph_ThenItsOperationIsRegisteredOnce()
    {
        // Arrange
        var container = new MethodTestContainer(CreateContext());
        var subject = new MethodTestSubject();
        container.Child = subject;
        container.Child = null;

        // Act
        container.Child = subject;

        // Assert
        var registered = subject.TryGetRegisteredSubject()!;
        Assert.Single(registered.Properties, property => property.Name == "Stop");
    }

    [Theory]
    [InlineData("Unlock", true)]
    [InlineData("ToggleLock", true)]
    [InlineData("Lock", false)]
    public void WhenASubjectImplementsTheDoorLockController_ThenOperationsThatCanUnlockRequireConfirmation(string methodName, bool expected)
    {
        // Arrange
        var subject = new DoorLockTestSubject(CreateContext());

        // Act
        var metadata = (MethodMetadata)subject.TryGetRegisteredSubject()!.TryGetProperty(methodName)!.GetValue()!;

        // Assert
        Assert.Equal(expected, metadata.RequiresConfirmation);
    }

    [Fact]
    public void WhenAConcreteOperationImplementsAnInterfaceOperationThatRequiresConfirmation_ThenItStillRequiresConfirmation()
    {
        // Arrange
        var subject = new OwnUnlockDoorLockTestSubject(CreateContext());

        // Act
        var unlock = GetMethod(subject, "Unlock");
        var lockMethod = GetMethod(subject, "Lock");

        // Assert
        Assert.Equal("Open", unlock.Title);
        Assert.True(unlock.RequiresConfirmation);
        Assert.Equal("Close", lockMethod.Title);
        Assert.False(lockMethod.RequiresConfirmation);
    }

    [Fact]
    public void WhenAQueryImplementsAnInterfaceOperationThatRequiresConfirmation_ThenItIsAnOperationThatRequiresConfirmation()
    {
        // Arrange
        var subject = new QueryUnlockDoorLockTestSubject(CreateContext());

        // Act
        var unlock = GetMethod(subject, "Unlock");

        // Assert
        Assert.Equal(MethodKind.Operation, unlock.Kind);
        Assert.True(unlock.RequiresConfirmation);
        Assert.Equal("Peek", unlock.Title);
    }

    [Fact]
    public void WhenAnOperationOverridesABaseOperationThatRequiresConfirmation_ThenItStillRequiresConfirmation()
    {
        // Arrange
        var subject = new OwnOperationOverrideSubject(CreateContext());

        // Act
        var reset = GetMethod(subject, "Reset");

        // Assert
        Assert.Equal("Reset now", reset.Title);
        Assert.True(reset.RequiresConfirmation);
    }

    [Fact]
    public void WhenAnIntermediateOverrideRequiresConfirmation_ThenAFurtherOverrideStillRequiresConfirmation()
    {
        // Arrange
        var subject = new FurtherOperationOverrideSubject(CreateContext());

        // Act
        var restart = GetMethod(subject, "Restart");

        // Assert
        Assert.Equal("Restart now", restart.Title);
        Assert.True(restart.RequiresConfirmation);
    }

    [Fact]
    public void WhenAnIntermediateOverrideOfAGenericMethodRequiresConfirmation_ThenAFurtherOverrideStillRequiresConfirmation()
    {
        // Arrange
        var subject = new FurtherGenericOperationOverrideSubject(CreateContext());

        // Act
        var apply = GetMethod(subject, "Apply");

        // Assert
        Assert.Equal("Apply now", apply.Title);
        Assert.True(apply.RequiresConfirmation);
    }

    [Fact]
    public void WhenASubjectHasNoMethods_ThenNoMethodPropertiesAreRegistered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new NoMethodsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var methods = registered.GetAllMethods();

        // Assert
        Assert.Empty(methods);
    }

    [Fact]
    public void WhenAMethodHasACategory_ThenTheMetadataKeepsIt()
    {
        // Arrange
        var subject = new CategorizedMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var operation = registered.TryGetProperty("Play")!.GetValue() as MethodMetadata;
        var query = registered.TryGetProperty("GetQueue")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Playback", operation!.Category);
        Assert.Equal("Queue", query!.Category);
    }

    [Fact]
    public void WhenAMethodHasNoCategory_ThenTheMetadataHasNone()
    {
        // Arrange
        var subject = new CategorizedMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Reboot")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.Category);
    }

    [Fact]
    public void WhenOnlyTheInterfaceMethodIsAnOperation_ThenTheMetadataHasTheCategoryOfTheInterfaceMethod()
    {
        // Arrange
        var subject = new CategorizedMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Mute")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Volume", metadata!.Category);
    }

    [Fact]
    public void WhenTheClassMethodIsAnOperationWithoutACategory_ThenTheMetadataHasTheCategoryOfTheInterfaceMethod()
    {
        // Arrange
        var subject = new InheritedCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("SetVolume")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Volume", metadata!.Category);
    }

    [Fact]
    public void WhenTheClassAndTheInterfaceMethodHaveACategory_ThenTheMetadataHasTheOneOfTheClass()
    {
        // Arrange
        var subject = new InheritedCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Mute")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Sound", metadata!.Category);
    }

    [Fact]
    public void WhenOnlyTheInterfaceMethodIsAnOperationAndTheClassMethodHasACategory_ThenTheMetadataHasTheOneOfTheClass()
    {
        // Arrange
        var subject = new InheritedCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Unmute")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Sound", metadata!.Category);
    }

    [Fact]
    public void WhenTheClassMethodHasAnEmptyCategory_ThenTheMetadataHasNoCategoryInsteadOfTheOneOfTheInterfaceMethod()
    {
        // Arrange
        var subject = new InheritedCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Reset")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.Category);
    }

    [Fact]
    public void WhenACategoryHasSurroundingWhitespace_ThenTheMetadataHasTheTrimmedName()
    {
        // Arrange
        var subject = new CategorizedMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Pause")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Playback", metadata!.Category);
    }

    [Fact]
    public void WhenTheInterfaceDeclaresOverloads_ThenTheMetadataHasTheCategoryOfTheOneWithTheSameParameterTypes()
    {
        // Arrange
        var subject = new InheritedCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Set")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal(typeof(int), Assert.Single(metadata!.Parameters).Type);
        Assert.Equal("Number", metadata.Category);
    }

    [Fact]
    public void WhenAnotherInterfaceDeclaresTheSameMethodWithAnotherCategory_ThenTheMetadataHasTheCategoryOfTheInterfaceWithTheOperation()
    {
        // Arrange
        var subject = new TwoInterfaceCategoryMethodSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var metadata = registered.TryGetProperty("Mute")!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Volume", metadata!.Category);
    }

    [Fact]
    public void WhenTwoTypesImplementTheSameInterfaceMethod_ThenEachHasItsOwnCategory()
    {
        // Arrange
        var context = CreateContext();

        // Act
        // Only the interface method is an operation, so both types look up the category of the same method.
        var withClassCategory = new ClassCategoryMethodSubject(context);
        var withoutClassCategory = new NoClassCategoryMethodSubject(context);

        // Assert
        Assert.Equal("Sound", GetMethod(withClassCategory, "Mute").Category);
        Assert.Equal("Volume", GetMethod(withoutClassCategory, "Mute").Category);
    }

    [Fact]
    public void WhenTheInterfaceMethodIsImplementedExplicitly_ThenTheMetadataHasTheCategoryOfTheInterfaceMethod()
    {
        // Arrange
        var subject = new ExplicitCategoryMethodSubject(CreateContext());

        // Act
        var metadata = GetMethod(subject, "Mute");

        // Assert
        Assert.Equal("Volume", metadata.Category);
    }

    [Fact]
    public void WhenAnOperationIsInheritedFromABaseClass_ThenTheMetadataKeepsItsCategory()
    {
        // Arrange
        var subject = new DerivedCategoryMethodSubject(CreateContext());

        // Act
        var metadata = GetMethod(subject, "Play");

        // Assert
        Assert.Equal("Playback", metadata.Category);
    }

    [Fact]
    public void WhenABaseClassImplementsTheInterfaceMethod_ThenTheMetadataKeepsTheCategories()
    {
        // Arrange
        var subject = new DerivedCategoryMethodSubject(CreateContext());

        // Act
        var withoutClassCategory = GetMethod(subject, "Mute");
        var withClassCategory = GetMethod(subject, "Unmute");

        // Assert
        Assert.Equal("Volume", withoutClassCategory.Category);
        Assert.Equal("Sound", withClassCategory.Category);
    }

    private static MethodMetadata GetMethod(IInterceptorSubject subject, string propertyName)
    {
        return (MethodMetadata)subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!.GetValue()!;
    }
}

public interface ICategoryOnlyMethodInterface
{
    [Category("Other")]
    Task MuteAsync();
}

public interface IOperationCategoryMethodInterface
{
    [Operation]
    [Category("Volume")]
    Task MuteAsync();

    [Operation]
    [Category("Volume")]
    Task UnmuteAsync();
}

// The interface without the operation comes first, as it would be found first among the interfaces of the type.
[InterceptorSubject]
public partial class TwoInterfaceCategoryMethodSubject : ICategoryOnlyMethodInterface, IOperationCategoryMethodInterface
{
    public Task MuteAsync() => Task.CompletedTask;

    public Task UnmuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ClassCategoryMethodSubject : IOperationCategoryMethodInterface
{
    [Category("Sound")]
    public Task MuteAsync() => Task.CompletedTask;

    public Task UnmuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class NoClassCategoryMethodSubject : IOperationCategoryMethodInterface
{
    public Task MuteAsync() => Task.CompletedTask;

    public Task UnmuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ExplicitCategoryMethodSubject : IOperationCategoryMethodInterface
{
    [Category("Sound")]
    Task IOperationCategoryMethodInterface.MuteAsync() => Task.CompletedTask;

    Task IOperationCategoryMethodInterface.UnmuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public abstract partial class CategoryMethodSubjectBase : IOperationCategoryMethodInterface
{
    [Operation]
    [Category("Playback")]
    public Task PlayAsync() => Task.CompletedTask;

    public Task MuteAsync() => Task.CompletedTask;

    [Category("Sound")]
    public Task UnmuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class DerivedCategoryMethodSubject : CategoryMethodSubjectBase;

public interface IInheritedCategoryMethodInterface
{
    [Category("Volume")]
    Task SetVolumeAsync(decimal volume);

    [Operation]
    [Category("Volume")]
    Task MuteAsync();

    [Operation]
    [Category("Volume")]
    Task UnmuteAsync();

    [Category("Volume")]
    Task ResetAsync();

    [Category("Text")]
    Task SetAsync(string value);

    [Category("Number")]
    Task SetAsync(int value);
}

[InterceptorSubject]
public partial class InheritedCategoryMethodSubject : IInheritedCategoryMethodInterface
{
    [Operation]
    public Task SetVolumeAsync(decimal volume) => Task.CompletedTask;

    [Operation]
    [Category("Sound")]
    public Task MuteAsync() => Task.CompletedTask;

    [Category("Sound")]
    public Task UnmuteAsync() => Task.CompletedTask;

    [Operation]
    [Category("")]
    public Task ResetAsync() => Task.CompletedTask;

    public Task SetAsync(string value) => Task.CompletedTask;

    [Operation]
    public Task SetAsync(int value) => Task.CompletedTask;
}

public interface ICategorizedMethodInterface
{
    [Operation]
    [Category("Volume")]
    Task MuteAsync();
}

[InterceptorSubject]
public partial class CategorizedMethodSubject : ICategorizedMethodInterface
{
    [Operation]
    [Category("Playback")]
    public Task PlayAsync() => Task.CompletedTask;

    [Operation]
    [Category(" Playback ")]
    public Task PauseAsync() => Task.CompletedTask;

    [Query]
    [Category("Queue")]
    public string GetQueue() => string.Empty;

    [Operation]
    public Task RebootAsync() => Task.CompletedTask;

    public Task MuteAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class MethodTestContainer
{
    public partial MethodTestSubject? Child { get; set; }
}

public interface IMethodTestInterface
{
    [Operation(Title = "Interface Op")]
    Task InterfaceOperationAsync();
}

[InterceptorSubject]
public partial class MethodTestSubject : IMethodTestInterface
{
    public bool StopCalled { get; set; }

    [Operation(Title = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        StopCalled = true;
        return Task.CompletedTask;
    }

    [Operation]
    public Task SetTargetAsync(decimal temperature)
    {
        return Task.CompletedTask;
    }

    [Query(Title = "Diagnostics", Icon = "diag-icon", Position = 2)]
    public Task<string> GetDiagnosticsAsync()
    {
        return Task.FromResult("diagnostics-result");
    }

    public Task InterfaceOperationAsync()
    {
        return Task.CompletedTask;
    }
}

public interface ITestLogger { }

[InterceptorSubject]
public partial class MethodWithSpecialParamsSubject
{
    public int LastValue { get; set; }
    public CancellationToken LastToken { get; set; }

    [Operation]
    public Task DoWorkAsync(int value, [FromServices] ITestLogger? logger, CancellationToken cancellationToken)
    {
        LastValue = value;
        LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class VoidMethodSubject
{
    public bool ResetCalled { get; set; }

    [Operation(Title = "Reset")]
    public void Reset()
    {
        ResetCalled = true;
    }
}

[InterceptorSubject]
public partial class ThrowingMethodSubject
{
    [Operation(Title = "Fail")]
    public void Fail()
    {
        throw new InvalidOperationException("Something went wrong");
    }

    [Operation(Title = "Fail Async")]
    public Task FailAsyncAsync()
    {
        throw new InvalidOperationException("Async failure");
    }
}

[InterceptorSubject]
public partial class DescribedMethodSubject
{
    [Operation(Title = "Shutdown", Description = "Shuts down the system safely")]
    public Task ShutdownAsync()
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class TrulyAsyncThrowingSubject
{
    [Operation(Title = "Fail After Await")]
    public async Task FailAfterAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("truly async failure");
    }
}

[InterceptorSubject]
public partial class MethodWithRequiredServiceSubject
{
    [Operation]
    public Task DoWorkAsync([FromServices] ITestLogger logger)
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class NoMethodsSubject
{
    public partial string? Name { get; set; }
}

[InterceptorSubject]
public partial class DoorLockTestSubject : HomeBlaze.Abstractions.Devices.Locks.IDoorLockController
{
    public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UnlockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class OwnUnlockDoorLockTestSubject : HomeBlaze.Abstractions.Devices.Locks.IDoorLockController
{
    [Operation(Title = "Close")]
    public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Operation(Title = "Open")]
    public Task UnlockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class QueryUnlockDoorLockTestSubject : HomeBlaze.Abstractions.Devices.Locks.IDoorLockController
{
    public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Query(Title = "Peek")]
    public Task UnlockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public abstract partial class ConfirmedOperationBaseSubject
{
    [Operation(RequiresConfirmation = true)]
    public virtual Task ResetAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class OwnOperationOverrideSubject : ConfirmedOperationBaseSubject
{
    [Operation(Title = "Reset now")]
    public override Task ResetAsync() => Task.CompletedTask;
}

public abstract class UnconfirmedOperationBaseSubject
{
    [Operation]
    public virtual Task RestartAsync() => Task.CompletedTask;
}

public abstract class ConfirmedOperationOverrideSubject : UnconfirmedOperationBaseSubject
{
    [Operation(RequiresConfirmation = true)]
    public override Task RestartAsync() => Task.CompletedTask;
}

[InterceptorSubject]
public partial class FurtherOperationOverrideSubject : ConfirmedOperationOverrideSubject
{
    [Operation(Title = "Restart now")]
    public override Task RestartAsync() => Task.CompletedTask;
}

public abstract class UnconfirmedGenericOperationBaseSubject
{
    [Operation]
    public virtual Task ApplyAsync<TValue>(TValue value) => Task.CompletedTask;
}

public abstract class ConfirmedGenericOperationOverrideSubject : UnconfirmedGenericOperationBaseSubject
{
    [Operation(RequiresConfirmation = true)]
    public override Task ApplyAsync<TValue>(TValue value) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class FurtherGenericOperationOverrideSubject : ConfirmedGenericOperationOverrideSubject
{
    [Operation(Title = "Apply now")]
    public override Task ApplyAsync<TValue>(TValue value) => Task.CompletedTask;
}
