using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.FritzBox;

public partial class FritzBox
{
    // Read and written by the polling loop only.
    private bool _isDeviceLinkFailureLogged;

    /// <summary>
    /// Sets <see cref="FritzBoxClient.DevicePath"/> of every client to the subject that
    /// <see cref="FritzBoxDeviceMatcher"/> finds for it. Does nothing without a registry and a path resolver.
    /// Called by the polling loop only.
    /// </summary>
    internal void ApplyDevicePaths()
    {
        var context = ((IInterceptorSubject)this).Context;
        var registry = context.TryGetService<ISubjectRegistry>();
        var pathResolver = context.TryGetService<ISubjectPathResolver>();
        if (registry is null || pathResolver is null)
        {
            return;
        }

        var clients = Clients.Values;
        var devices = FritzBoxDeviceMatcher.Match(registry.KnownSubjects.Keys, clients);
        foreach (var client in clients)
        {
            client.DevicePath = devices.TryGetValue(client, out var device)
                ? pathResolver.GetPath(device, PathStyle.Canonical)
                : null;
        }
    }

    private void TryApplyDevicePaths()
    {
        // Linking reads other subjects, so a failure there says nothing about the router and does not fail the poll.
        try
        {
            ApplyDevicePaths();
            _isDeviceLinkFailureLogged = false;
        }
        catch (Exception exception)
        {
            if (!_isDeviceLinkFailureLogged)
            {
                _isDeviceLinkFailureLogged = true;
                _logger.LogWarning(exception, "FRITZ!Box {HostAddress}: linking the clients to their devices failed.", HostAddress);
            }
        }
    }
}
