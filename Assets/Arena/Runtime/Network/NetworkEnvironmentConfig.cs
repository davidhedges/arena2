#nullable enable

using System.Text;
using UnityEngine;

namespace Arena.Network
{
    internal enum NetworkEnvironmentKind
    {
        Local,
        Remote,
        Custom,
    }

    internal readonly struct NetworkEnvironmentEndpoint
    {
        internal NetworkEnvironmentEndpoint(
            NetworkEnvironmentKind kind,
            string displayName,
            string serverUri,
            string moduleName)
        {
            Kind = kind;
            DisplayName = displayName;
            ServerUri = serverUri;
            ModuleName = moduleName;
        }

        internal NetworkEnvironmentKind Kind { get; }
        internal string DisplayName { get; }
        internal string ServerUri { get; }
        internal string ModuleName { get; }
    }

    internal static class NetworkEnvironmentConfig
    {
        internal const string DefaultModuleName = "arena";
        internal const string LocalServerUri = "ws://localhost:3000";
        internal const string RemoteServerUri = "wss://arena.meandmyson.org";

        private const string EnvironmentPrefsKey = "arena.network.environment";
        private const string AuthTokenPrefsPrefix = "arena.network.auth_token.";

        internal static NetworkEnvironmentKind CurrentEnvironment
        {
            get
            {
                string stored = PlayerPrefs.GetString(
                    EnvironmentPrefsKey,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    NetworkEnvironmentKind.Local.ToString()
#else
                    NetworkEnvironmentKind.Remote.ToString()
#endif
                );

                return System.Enum.TryParse(stored, true, out NetworkEnvironmentKind parsed)
                    && parsed != NetworkEnvironmentKind.Custom
                    ? parsed
                    : DefaultEnvironment;
            }
        }

        internal static NetworkEnvironmentEndpoint CurrentEndpoint => EndpointFor(CurrentEnvironment);

        private static NetworkEnvironmentKind DefaultEnvironment
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return NetworkEnvironmentKind.Local;
#else
                return NetworkEnvironmentKind.Remote;
#endif
            }
        }

        internal static void SetCurrentEnvironment(NetworkEnvironmentKind environment)
        {
            if (environment == NetworkEnvironmentKind.Custom)
                return;

            PlayerPrefs.SetString(EnvironmentPrefsKey, environment.ToString());
            PlayerPrefs.Save();
        }

        internal static NetworkEnvironmentEndpoint EndpointFor(NetworkEnvironmentKind environment)
        {
            return environment switch
            {
                NetworkEnvironmentKind.Remote => new NetworkEnvironmentEndpoint(
                    NetworkEnvironmentKind.Remote,
                    "Remote",
                    RemoteServerUri,
                    DefaultModuleName),
                _ => new NetworkEnvironmentEndpoint(
                    NetworkEnvironmentKind.Local,
                    "Local",
                    LocalServerUri,
                    DefaultModuleName),
            };
        }

        internal static NetworkEnvironmentEndpoint ResolveEndpoint(
            bool useSerializedEndpointOverride,
            string serializedServerUri,
            string serializedModuleName)
        {
            if (!useSerializedEndpointOverride || string.IsNullOrWhiteSpace(serializedServerUri))
                return CurrentEndpoint;

            return new NetworkEnvironmentEndpoint(
                NetworkEnvironmentKind.Custom,
                "Custom",
                serializedServerUri.Trim(),
                string.IsNullOrWhiteSpace(serializedModuleName)
                    ? DefaultModuleName
                    : serializedModuleName.Trim());
        }

        internal static string? LoadAuthToken(NetworkEnvironmentEndpoint endpoint)
        {
            string token = PlayerPrefs.GetString(TokenPrefsKey(endpoint), string.Empty);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        internal static void SaveAuthToken(NetworkEnvironmentEndpoint endpoint, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            PlayerPrefs.SetString(TokenPrefsKey(endpoint), token);
            PlayerPrefs.Save();
        }

        internal static void ClearAuthToken(NetworkEnvironmentEndpoint endpoint)
        {
            PlayerPrefs.DeleteKey(TokenPrefsKey(endpoint));
            PlayerPrefs.Save();
        }

        private static string TokenPrefsKey(NetworkEnvironmentEndpoint endpoint)
            => AuthTokenPrefsPrefix + SanitizeKeyPart($"{endpoint.ModuleName}|{endpoint.ServerUri}");

        private static string SanitizeKeyPart(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            }

            return sb.ToString();
        }
    }
}
