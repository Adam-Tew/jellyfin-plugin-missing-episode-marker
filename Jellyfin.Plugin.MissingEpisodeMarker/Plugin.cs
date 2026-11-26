using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MissingEpisodeMarker;

/// <summary>
/// Missing Episode Marker plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's stable id. Never change this after the first release.</summary>
    public const string PluginGuid = "2c1c05ff-9147-4666-9eff-1daad3f44e07";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Secret for Sonarr's webhook URL; created once, can be regenerated on the settings page.
        if (string.IsNullOrWhiteSpace(Configuration.WebhookToken))
        {
            Configuration.WebhookToken = NewWebhookToken();
            SaveConfiguration();
        }
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Missing Episode Marker";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Marks show and season posters with what Sonarr is doing: missing episodes, stalled or running downloads, and new episodes on the way.";

    /// <summary>Creates a new random webhook token.</summary>
    /// <returns>A 32-character hex token.</returns>
    public static string NewWebhookToken()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Puts every original poster back before the plugin is removed,
    /// so no series is left pointing at a marked copy.
    /// </summary>
    public override void OnUninstalling()
    {
        try
        {
            Posters.PosterMarkerService.Current?.RestoreAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // Never block uninstalling.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "MissingEpisodeMarker",
                DisplayName = "Missing Episode Marker",
                EnableInMainMenu = Instance?.Configuration.ShowInMenu ?? false,
                MenuIcon = "broken_image",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        ];
    }
}
