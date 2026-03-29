using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Whisper;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Jellyfin plugin that processes media files with whisper.cpp to generate
/// word-level transcriptions stored alongside media files.
/// </summary>
public class WhisperPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperPlugin"/> class.
    /// </summary>
    public WhisperPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static WhisperPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Whisper Transcription";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a4c5f23e-8b1d-4f6a-9c3e-7d2b8e1f0a5c");

    /// <inheritdoc />
    public override string Description => "Processes media files with whisper.cpp to generate word-level transcriptions.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        };
    }
}
