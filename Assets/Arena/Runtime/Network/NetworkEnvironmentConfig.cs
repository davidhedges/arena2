#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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
        internal const string LocalHubModuleName = "arena-hub-local";
        internal const string RemoteHubModuleName = "arena-hub";
        internal const string LocalServerUri = "ws://localhost:3000";
        internal const string RemoteServerUri = "wss://arena.meandmyson.org";

        private const string EnvironmentPrefsKey = "arena.network.environment";
        private const string LegacyAuthTokenPrefsPrefix = "arena.network.auth_token.";
        private const string CredentialService = "Arena.SpacetimeDB.Identity";
        private static readonly Dictionary<string, string> SessionAuthTokens = new();
        private static bool _warnedAboutSessionOnlyTokenStorage;

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

        internal static NetworkEnvironmentEndpoint CurrentHubEndpoint => HubEndpointFor(CurrentEnvironment);

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

        internal static NetworkEnvironmentEndpoint HubEndpointFor(NetworkEnvironmentKind environment)
        {
            NetworkEnvironmentEndpoint gameplay = EndpointFor(environment);
            return new NetworkEnvironmentEndpoint(
                gameplay.Kind,
                $"{gameplay.DisplayName} Hub",
                gameplay.ServerUri,
                environment == NetworkEnvironmentKind.Remote
                    ? RemoteHubModuleName
                    : LocalHubModuleName);
        }

        internal static NetworkEnvironmentEndpoint HubEndpointFor(NetworkEnvironmentEndpoint gameplayEndpoint)
        {
            string moduleName = gameplayEndpoint.Kind == NetworkEnvironmentKind.Remote
                ? RemoteHubModuleName
                : LocalHubModuleName;
            return new NetworkEnvironmentEndpoint(
                gameplayEndpoint.Kind,
                $"{gameplayEndpoint.DisplayName} Hub",
                gameplayEndpoint.ServerUri,
                moduleName);
        }

        internal static NetworkEnvironmentEndpoint ResolveEndpoint(
            bool useSerializedEndpointOverride,
            string serializedServerUri,
            string serializedModuleName)
        {
            if (Application.isBatchMode)
            {
                string? headlessModule = Environment.GetEnvironmentVariable("ARENA_HEADLESS_MODULE");
                if (!string.IsNullOrWhiteSpace(headlessModule))
                {
                    string? headlessServer = Environment.GetEnvironmentVariable("ARENA_HEADLESS_SERVER_URI");
                    return new NetworkEnvironmentEndpoint(
                        NetworkEnvironmentKind.Custom,
                        "Headless",
                        string.IsNullOrWhiteSpace(headlessServer)
                            ? LocalServerUri
                            : headlessServer.Trim(),
                        headlessModule.Trim());
                }
            }

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
            string account = CredentialAccount(endpoint);
            if (SessionAuthTokens.TryGetValue(account, out string sessionToken)
                && !string.IsNullOrWhiteSpace(sessionToken))
            {
                return sessionToken;
            }

            if (PlatformCredentialStore.TryLoad(CredentialService, account, out string secureToken)
                && !string.IsNullOrWhiteSpace(secureToken))
            {
                SessionAuthTokens[account] = secureToken;
                DeleteLegacyPlaintextTokens(endpoint);
                return secureToken;
            }

            // Builds before the Hub/match split stored one token per database.
            // Prefer the original gameplay identity when upgrading from that
            // layout, then move it into the host-scoped account. This preserves
            // one SpacetimeDB identity while moving between databases.
            foreach (string legacyAccount in LegacyCredentialAccounts(endpoint))
            {
                if (SessionAuthTokens.TryGetValue(legacyAccount, out string legacySessionToken)
                    && !string.IsNullOrWhiteSpace(legacySessionToken))
                {
                    SaveMigratedAuthToken(endpoint, account, legacyAccount, legacySessionToken);
                    return legacySessionToken;
                }

                if (PlatformCredentialStore.TryLoad(
                        CredentialService,
                        legacyAccount,
                        out string legacySecureToken)
                    && !string.IsNullOrWhiteSpace(legacySecureToken))
                {
                    SaveMigratedAuthToken(endpoint, account, legacyAccount, legacySecureToken);
                    return legacySecureToken;
                }
            }

            // One-time migration from both plaintext locations used by older
            // Arena builds and by the SDK helper. The old keys are deleted
            // immediately even if this platform only supports session storage.
            string legacyToken = string.Empty;
            foreach (string legacyKey in LegacyTokenPrefsKeys(endpoint))
            {
                legacyToken = PlayerPrefs.GetString(legacyKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(legacyToken))
                    break;
            }
            if (string.IsNullOrWhiteSpace(legacyToken))
                legacyToken = PlayerPrefs.GetString(LegacySdkTokenPrefsKey(), string.Empty);

            DeleteLegacyPlaintextTokens(endpoint);
            if (string.IsNullOrWhiteSpace(legacyToken))
                return null;

            SaveAuthToken(endpoint, legacyToken);
            return legacyToken;
        }

        internal static void SaveAuthToken(NetworkEnvironmentEndpoint endpoint, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            string account = CredentialAccount(endpoint);
            SessionAuthTokens[account] = token;
            DeleteLegacyPlaintextTokens(endpoint);

            bool savedSecurely = PlatformCredentialStore.TrySave(
                CredentialService,
                account,
                token);
            if (savedSecurely)
            {
                foreach (string legacyAccount in LegacyCredentialAccounts(endpoint))
                {
                    SessionAuthTokens.Remove(legacyAccount);
                    PlatformCredentialStore.TryDelete(CredentialService, legacyAccount);
                }
            }

            if (!savedSecurely && !_warnedAboutSessionOnlyTokenStorage)
            {
                _warnedAboutSessionOnlyTokenStorage = true;
                Debug.LogWarning(
                    "[NetworkEnvironment] Secure credential storage is unavailable on this platform. "
                    + "The identity token will be retained for this process only.");
            }
        }

        internal static void ClearAuthToken(NetworkEnvironmentEndpoint endpoint)
        {
            string account = CredentialAccount(endpoint);
            SessionAuthTokens.Remove(account);
            PlatformCredentialStore.TryDelete(CredentialService, account);
            foreach (string legacyAccount in LegacyCredentialAccounts(endpoint))
            {
                SessionAuthTokens.Remove(legacyAccount);
                PlatformCredentialStore.TryDelete(CredentialService, legacyAccount);
            }
            DeleteLegacyPlaintextTokens(endpoint);
        }

        private static string CredentialAccount(NetworkEnvironmentEndpoint endpoint)
            => $"cluster|{CredentialScopeForServer(endpoint.ServerUri)}";

        /// <summary>
        /// Converts HTTP/WebSocket spellings of the same SpacetimeDB host to
        /// one credential scope. Loopback aliases are also equivalent so the
        /// local provisioner's 127.0.0.1 assignment reuses a localhost token.
        /// </summary>
        internal static string CredentialScopeForServer(string serverUri)
        {
            if (!Uri.TryCreate(serverUri?.Trim(), UriKind.Absolute, out Uri? uri))
                return (serverUri ?? string.Empty).Trim().ToLowerInvariant();

            string host = uri.IsLoopback
                ? "loopback"
                : uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            int port = uri.IsDefaultPort
                ? DefaultPortFor(uri.Scheme)
                : uri.Port;
            return string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        }

        private static int DefaultPortFor(string scheme)
            => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase)
                ? 443
                : 80;

        private static IEnumerable<string> LegacyCredentialAccounts(NetworkEnvironmentEndpoint endpoint)
        {
            // The gameplay module is first because it was the persistent
            // pre-split connection and therefore owns the identity to retain.
            string gameplayAccount = LegacyDatabaseCredentialAccount(
                endpoint.ServerUri,
                DefaultModuleName);
            yield return gameplayAccount;

            string endpointAccount = LegacyDatabaseCredentialAccount(
                endpoint.ServerUri,
                endpoint.ModuleName);
            if (!string.Equals(endpointAccount, gameplayAccount, StringComparison.Ordinal))
                yield return endpointAccount;
        }

        private static string LegacyDatabaseCredentialAccount(string serverUri, string moduleName)
            => $"{moduleName}|{serverUri}";

        private static void SaveMigratedAuthToken(
            NetworkEnvironmentEndpoint endpoint,
            string account,
            string legacyAccount,
            string token)
        {
            SessionAuthTokens[account] = token;
            if (PlatformCredentialStore.TrySave(CredentialService, account, token))
            {
                SessionAuthTokens.Remove(legacyAccount);
                PlatformCredentialStore.TryDelete(CredentialService, legacyAccount);
            }
            DeleteLegacyPlaintextTokens(endpoint);
        }

        private static IEnumerable<string> LegacyTokenPrefsKeys(NetworkEnvironmentEndpoint endpoint)
        {
            foreach (string legacyAccount in LegacyCredentialAccounts(endpoint))
                yield return LegacyAuthTokenPrefsPrefix + SanitizeKeyPart(legacyAccount);
        }

        private static string LegacySdkTokenPrefsKey()
        {
            string key = "spacetimedb.identity_token";
#if UNITY_EDITOR
            key += $" - {Application.dataPath}";
#endif
            return key;
        }

        private static void DeleteLegacyPlaintextTokens(NetworkEnvironmentEndpoint endpoint)
        {
            bool changed = false;
            foreach (string endpointKey in LegacyTokenPrefsKeys(endpoint))
            {
                if (PlayerPrefs.HasKey(endpointKey))
                {
                    PlayerPrefs.DeleteKey(endpointKey);
                    changed = true;
                }
            }

            string sdkKey = LegacySdkTokenPrefsKey();
            if (PlayerPrefs.HasKey(sdkKey))
            {
                PlayerPrefs.DeleteKey(sdkKey);
                changed = true;
            }

            if (changed)
                PlayerPrefs.Save();
        }

        private static string SanitizeKeyPart(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            }

            return sb.ToString();
        }

        private static class PlatformCredentialStore
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            private const string SecurityFramework =
                "/System/Library/Frameworks/Security.framework/Versions/A/Security";
            private const string CoreFoundationFramework =
                "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation";
            private const int Success = 0;

            [DllImport(SecurityFramework)]
            private static extern int SecKeychainFindGenericPassword(
                IntPtr keychainOrArray,
                uint serviceNameLength,
                byte[] serviceName,
                uint accountNameLength,
                byte[] accountName,
                out uint passwordLength,
                out IntPtr passwordData,
                out IntPtr itemRef);

            [DllImport(SecurityFramework)]
            private static extern int SecKeychainAddGenericPassword(
                IntPtr defaultKeychain,
                uint serviceNameLength,
                byte[] serviceName,
                uint accountNameLength,
                byte[] accountName,
                uint passwordLength,
                byte[] passwordData,
                out IntPtr itemRef);

            [DllImport(SecurityFramework)]
            private static extern int SecKeychainItemModifyAttributesAndData(
                IntPtr itemRef,
                IntPtr attrList,
                uint length,
                byte[] data);

            [DllImport(SecurityFramework)]
            private static extern int SecKeychainItemDelete(IntPtr itemRef);

            [DllImport(SecurityFramework)]
            private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

            [DllImport(CoreFoundationFramework)]
            private static extern void CFRelease(IntPtr value);

            internal static bool TryLoad(string service, string account, out string token)
            {
                token = string.Empty;
                byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
                byte[] accountBytes = Encoding.UTF8.GetBytes(account);
                IntPtr passwordData = IntPtr.Zero;
                IntPtr itemRef = IntPtr.Zero;

                try
                {
                    int status = SecKeychainFindGenericPassword(
                        IntPtr.Zero,
                        (uint)serviceBytes.Length,
                        serviceBytes,
                        (uint)accountBytes.Length,
                        accountBytes,
                        out uint passwordLength,
                        out passwordData,
                        out itemRef);
                    if (status != Success || passwordData == IntPtr.Zero)
                        return false;

                    byte[] tokenBytes = new byte[checked((int)passwordLength)];
                    Marshal.Copy(passwordData, tokenBytes, 0, tokenBytes.Length);
                    token = Encoding.UTF8.GetString(tokenBytes);
                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkEnvironment] Keychain read failed: {e.Message}");
                    token = string.Empty;
                    return false;
                }
                finally
                {
                    if (passwordData != IntPtr.Zero)
                        SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                    if (itemRef != IntPtr.Zero)
                        CFRelease(itemRef);
                }
            }

            internal static bool TrySave(string service, string account, string token)
            {
                byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
                byte[] accountBytes = Encoding.UTF8.GetBytes(account);
                byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
                IntPtr passwordData = IntPtr.Zero;
                IntPtr itemRef = IntPtr.Zero;

                try
                {
                    int findStatus = SecKeychainFindGenericPassword(
                        IntPtr.Zero,
                        (uint)serviceBytes.Length,
                        serviceBytes,
                        (uint)accountBytes.Length,
                        accountBytes,
                        out _,
                        out passwordData,
                        out itemRef);
                    if (passwordData != IntPtr.Zero)
                    {
                        SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                        passwordData = IntPtr.Zero;
                    }

                    if (findStatus == Success && itemRef != IntPtr.Zero)
                    {
                        return SecKeychainItemModifyAttributesAndData(
                            itemRef,
                            IntPtr.Zero,
                            (uint)tokenBytes.Length,
                            tokenBytes) == Success;
                    }

                    int addStatus = SecKeychainAddGenericPassword(
                        IntPtr.Zero,
                        (uint)serviceBytes.Length,
                        serviceBytes,
                        (uint)accountBytes.Length,
                        accountBytes,
                        (uint)tokenBytes.Length,
                        tokenBytes,
                        out IntPtr addedItemRef);
                    if (addedItemRef != IntPtr.Zero)
                        CFRelease(addedItemRef);
                    return addStatus == Success;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkEnvironment] Keychain write failed: {e.Message}");
                    return false;
                }
                finally
                {
                    if (passwordData != IntPtr.Zero)
                        SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                    if (itemRef != IntPtr.Zero)
                        CFRelease(itemRef);
                }
            }

            internal static bool TryDelete(string service, string account)
            {
                byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
                byte[] accountBytes = Encoding.UTF8.GetBytes(account);
                IntPtr passwordData = IntPtr.Zero;
                IntPtr itemRef = IntPtr.Zero;

                try
                {
                    int status = SecKeychainFindGenericPassword(
                        IntPtr.Zero,
                        (uint)serviceBytes.Length,
                        serviceBytes,
                        (uint)accountBytes.Length,
                        accountBytes,
                        out _,
                        out passwordData,
                        out itemRef);
                    if (status != Success || itemRef == IntPtr.Zero)
                        return false;
                    return SecKeychainItemDelete(itemRef) == Success;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkEnvironment] Keychain delete failed: {e.Message}");
                    return false;
                }
                finally
                {
                    if (passwordData != IntPtr.Zero)
                        SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                    if (itemRef != IntPtr.Zero)
                        CFRelease(itemRef);
                }
            }
#else
            internal static bool TryLoad(string service, string account, out string token)
            {
                token = string.Empty;
                return false;
            }

            internal static bool TrySave(string service, string account, string token) => false;

            internal static bool TryDelete(string service, string account) => false;
#endif
        }
    }
}
