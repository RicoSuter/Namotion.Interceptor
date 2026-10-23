using HomeBlaze;

// Top-level statements are kept deliberately: WebApplicationFactory in the end-to-end tests resolves this
// assembly's entry point, so the generated Program type has to stay.
var application = await HomeBlazeWebApplication.CreateAsync(
    new HomeBlazeWebApplicationOptions { CommandLineArguments = args },
    CancellationToken.None);

await application.RunAsync();
