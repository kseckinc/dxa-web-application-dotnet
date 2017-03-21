﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Configuration;
using Sdl.Web.Common.Interfaces;
using Sdl.Web.Common.Logging;
using Sdl.Web.Common.Models;

namespace Sdl.Web.Common.Configuration
{
    /// <summary>
    /// Represents configuration that applies to the entire web application.
    /// </summary>
    public static class SiteConfiguration
    {
        public const string VersionRegex = "(v\\d*.\\d*)";
        public const string SystemFolder = "system";
        public const string CoreModuleName = "core";
        public const string StaticsFolder = "BinaryData";
        public const string DefaultVersion = "v1.00";

        //A set of refresh states, keyed by localization id and then type (eg "config", "resources" etc.) 
        private static readonly Dictionary<string, Dictionary<string, DateTime>> _refreshStates = new Dictionary<string, Dictionary<string, DateTime>>();
        //A set of locks to use, one per localization
        private static readonly Dictionary<string, object> _localizationLocks = new Dictionary<string, object>();
        //A global lock
        private static readonly object _lock = new object();
        private static string _defaultModuleName;

        #region References to "providers"
        /// <summary>
        /// Gets the Logger (Logging Provider)
        /// </summary>
        /// <remarks>
        /// This is only set if a Logger is configured explicitly.
        /// Avoid using this property directly.  For logging, use class <see cref="Log"/>.
        /// </remarks>
        public static ILogger Logger { get; private set; }

        /// <summary>
        /// Gets the Cache Provider.
        /// </summary>
        public static ICacheProvider CacheProvider { get; private set; }

        /// <summary>
        /// Gets the Content Provider used for obtaining the Page and Entity Models and Static Content.
        /// </summary>
        public static IContentProvider ContentProvider { get; private set; }

        /// <summary>
        /// Gets the Content Provider used for obtaining the Navigation Models
        /// </summary>.
        public static INavigationProvider NavigationProvider { get; private set; }

        /// <summary>
        /// Gets the Context Claims Provider.
        /// </summary>
        public static IContextClaimsProvider ContextClaimsProvider { get; private set; }

        /// <summary>
        /// Gets the Link Resolver.
        /// </summary>
        public static ILinkResolver LinkResolver { get; private set; }

        /// <summary>
        /// Gets the Rich Text Processor.
        /// </summary>
        public static IRichTextProcessor RichTextProcessor { get; private set; }

        /// <summary>
        /// Gets the Conditional Entity Evaluator.
        /// </summary>
        public static IConditionalEntityEvaluator ConditionalEntityEvaluator { get; private set; }

        /// <summary>
        /// Gets the Media helper used for generating responsive markup for images, videos etc.
        /// </summary>
        public static IMediaHelper MediaHelper  { get; private set; }

        /// <summary>
        /// Gets the Localization Resolver used for mapping URLs to Localizations.
        /// </summary>
        public static ILocalizationResolver LocalizationResolver { get; private set; }

        /// <summary>
        /// Gets the Handler for Unknown Localizations (failed publication URL lookups).
        /// </summary>
        public static IUnknownLocalizationHandler UnknownLocalizationHandler {get; private set; }

        /// <summary>
        /// Initializes the providers (Content Provider, Link Resolver, Media Helper, etc.) using dependency injection, i.e. obtained from configuration.
        /// </summary>
        /// <param name="dependencyResolver">A delegate that provide an implementation instance for a given interface type.</param>
        /// <remarks>
        /// This method took a parameter of type <see cref="System.Web.Mvc.IDependencyResolver"/> in DXA 1.1 and 1.2.
        /// That created an undesirable dependency on System.Web.Mvc and therefore this has been changed to a delegate in DXA 1.3.
        /// We couldn't keep the old signature (as deprecated) because that would still result in a dependency on System.Web.Mvc.
        /// This means that the call in Global.asax.cs must be changed from
        /// <c>SiteConfiguration.InitializeProviders(DependencyResolver.Current);</c> to
        /// <c>SiteConfiguration.InitializeProviders(DependencyResolver.Current.GetService);</c>
        /// </remarks>
        public static void InitializeProviders(Func<Type, object> dependencyResolver)
        {
            // Initialize the Logger before logging anything:
            Logger = (ILogger) dependencyResolver(typeof(ILogger));

            using (new Tracer())
            {
                string assemblyFileVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
                Log.Info("-------- Initializing DXA Framework v{0} --------", assemblyFileVersion);

                if (Logger != null)
                {
                    Log.Info("Using implementation type '{0}' for interface ILogger.", Logger.GetType().FullName);
                }

                CacheProvider = GetProvider<ICacheProvider>(dependencyResolver);
                ContentProvider = GetProvider<IContentProvider>(dependencyResolver);
                NavigationProvider = GetProvider<INavigationProvider>(dependencyResolver);
                ContextClaimsProvider = GetProvider<IContextClaimsProvider>(dependencyResolver);
                LinkResolver = GetProvider<ILinkResolver>(dependencyResolver);
                RichTextProcessor = GetProvider<IRichTextProcessor>(dependencyResolver);
                ConditionalEntityEvaluator = GetProvider<IConditionalEntityEvaluator>(dependencyResolver, isOptional: true);
                MediaHelper = GetProvider<IMediaHelper>(dependencyResolver);
                LocalizationResolver = GetProvider<ILocalizationResolver>(dependencyResolver);
                UnknownLocalizationHandler = GetProvider<IUnknownLocalizationHandler>(dependencyResolver, isOptional: true);
            }
        }

        private static T GetProvider<T>(Func<Type, object> dependencyResolver, bool isOptional = false)
            where T: class // interface to be more precise.
        {
            Type interfaceType = typeof(T);
            T provider = (T) dependencyResolver(interfaceType);
            if (provider == null)
            {
                if (!isOptional)
                {
                    throw new DxaException(String.Format("No implementation type configured for interface {0}. Check your Unity.config.", interfaceType.Name));
                }
                Log.Info("No implementation type configured for optional interface {0}.", interfaceType.Name);
            }
            else
            {
                Log.Info("Using implementation type '{0}' for interface {1}.", provider.GetType().FullName, interfaceType.Name);
            }
            return provider;
        }
        #endregion


        public static string GetPageController()
        {
            return "Page";
        }

        public static string GetPageAction()
        {
            return "Page";
        }

        public static string GetRegionController()
        {
            return "Region";
        }

        public static string GetRegionAction()
        {
            return "Region";
        }

        public static string GetEntityController()
        {
            return "Entity";
        }

        public static string GetEntityAction()
        {
            return "Entity";
        }

        public static string GetDefaultModuleName()
        {
            if (_defaultModuleName == null)
            {
                // Might come here multiple times in case of a race condition, but that doesn't matter.
                string defaultModuleSetting = WebConfigurationManager.AppSettings["default-module"];
                _defaultModuleName = string.IsNullOrEmpty(defaultModuleSetting) ? "Core" : defaultModuleSetting;
                Log.Debug("Default Module Name: '{0}'", _defaultModuleName);
            }

            return _defaultModuleName;
        }

        /// <summary>
        /// Removes the version number from a URL path for an asset
        /// </summary>
        /// <param name="path">The URL path</param>
        /// <returns>The 'real' path to the asset</returns>
        public static String RemoveVersionFromPath(string path)
        {
            return Regex.Replace(path, SystemFolder + "/" + VersionRegex + "/", delegate
            {
                return SystemFolder + "/";
            });
        }

        /// <summary>
        /// Ensure that a URL is using the path to the given localization
        /// </summary>
        /// <param name="url">The URL to localize</param>
        /// <param name="localization">The localization to use</param>
        /// <returns>A localized URL</returns>
        [Obsolete("Deprecated in DXA 2.0. Use Localization.GetAbsoluteUrlPath instead.")]
        public static string LocalizeUrl(string url, Localization localization)
            => localization.GetAbsoluteUrlPath(url);

        /// <summary>
        /// Take a partial URL (so not including protocol, domain, port) and make it full by
        /// Adding the protocol, domain, port etc. from the given localization
        /// </summary>
        public static string MakeFullUrl(string url, Localization loc)
        {
            if (url.StartsWith(loc.Path))
            {
                url = url.Substring(loc.Path.Length);
            }
            return url.StartsWith("http") ? url : loc.GetBaseUrl() + url;
        }

        [Obsolete("Deprecated in DXA 2.0. Use Localization.BinaryCacheFolder instead.")]
        public static string GetLocalStaticsFolder(string localizationId)
            => LocalizationResolver.GetLocalization(localizationId).BinaryCacheFolder;

        [Obsolete("Deprecated in DXA 2.0. Use Localization.BinaryCacheFolder instead.")]
        public static string GetLocalStaticsUrl(string localizationId)
            => LocalizationResolver.GetLocalization(localizationId).BinaryCacheFolder.Replace("\\","/");

        /// <summary>
        /// Generic a GUID
        /// </summary>
        /// <param name="prefix">prefix for the GUID</param>
        /// <returns>Prefixed Unique Identifier</returns>
        public static string GetUniqueId(string prefix)
        {
            return prefix + Guid.NewGuid().ToString("N");
        }

        #region Thread Safe Settings Update Helper Methods
        public static bool CheckSettingsNeedRefresh(string type, Localization localization) // TODO: Move to class Localization
        {
            Dictionary<string, DateTime> localizationRefreshStates;
            if (!_refreshStates.TryGetValue(localization.Id, out localizationRefreshStates))
            {
                return false;
            }
            DateTime settingsRefresh;
            if (!localizationRefreshStates.TryGetValue(type, out settingsRefresh))
            {
                return false;
            }
            return settingsRefresh.AddSeconds(1) < localization.LastRefresh;
        }

        public static void ThreadSafeSettingsUpdate<T>(string type, Dictionary<string, T> settings, string localizationId, T value) // TODO
        {
            lock (GetLocalizationLock(localizationId))
            {
                settings[localizationId] = value;
                UpdateRefreshState(localizationId, type);
            }
        }

        private static void UpdateRefreshState(string localizationId, string type) // TODO
        {
            //Update is already done under a localization lock, so we don't need to lock again here
            if (!_refreshStates.ContainsKey(localizationId))
            {
                _refreshStates.Add(localizationId, new Dictionary<string, DateTime>());
            }
            Dictionary<string, DateTime> states = _refreshStates[localizationId];
            if (states.ContainsKey(type))
            {
                states[type] = DateTime.Now;
            }
            else
            {
                states.Add(type, DateTime.Now);
            }
        }

        private static object GetLocalizationLock(string localizationId)
        {
            if (!_localizationLocks.ContainsKey(localizationId))
            {
                lock (_lock)
                {
                    _localizationLocks.Add(localizationId, new object());
                }
            }
            return _localizationLocks[localizationId];
        }

        #endregion
    }
}
