using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using p3ppc.P3RShuffleTime.Configuration;

namespace p3ppc.P3RShuffleTime.Template
{
    public class ModBase
    {
        public virtual bool CanSuspend() => false;
        public virtual bool CanUnload() => false;
        public virtual void Suspend() { }
        public virtual void Resume() { }
        public virtual void Unload() { }
        public virtual void Disposing() { }
        public virtual void ConfigurationUpdated(Config configuration) { }
    }

    public sealed class ModContext
    {
        public IModLoader ModLoader { get; init; } = null!;
        public IReloadedHooks? Hooks { get; init; }
        public ILogger Logger { get; init; } = null!;
        public Config Configuration { get; init; } = null!;
        public IModConfig ModConfig { get; init; } = null!;
        public IMod Owner { get; init; } = null!;
    }

    public sealed class Startup : IMod
    {
        private ModBase _mod = new Mod();
        private Config _configuration = null!;

        public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
        {
            var modLoader = (IModLoader)loaderApi;
            var currentMod = (IModConfig)modConfig;
            var logger = (ILogger)modLoader.GetLogger();
            IReloadedHooks? hooks = null;
            var hooksController = modLoader.GetController<IReloadedHooks>();
            if (hooksController != null)
            {
                hooksController.TryGetTarget(out hooks);
            }

            var configurator = new Configuration.Configurator(modLoader.GetModConfigDirectory(currentMod.ModId));
            configurator.SetContext(new ConfiguratorContext { Application = modLoader.GetAppConfig() });
            _configuration = configurator.GetConfiguration<Config>(0);
            _configuration.ConfigurationUpdated += OnConfigurationUpdated;

            _mod = new Mod(new ModContext
            {
                Logger = logger,
                Hooks = hooks,
                ModLoader = modLoader,
                ModConfig = currentMod,
                Owner = this,
                Configuration = _configuration
            });
        }

        private void OnConfigurationUpdated(IUpdatableConfigurable configurable)
        {
            _configuration = (Config)configurable;
            _mod.ConfigurationUpdated(_configuration);
        }

        public void Suspend() => _mod.Suspend();
        public void Resume() => _mod.Resume();
        public void Unload() => _mod.Unload();
        public bool CanUnload() => _mod.CanUnload();
        public bool CanSuspend() => _mod.CanSuspend();
        public Action Disposing => _mod.Disposing;
    }
}

namespace p3ppc.P3RShuffleTime.Template.Configuration
{
    public class Configurable<T> : IUpdatableConfigurable where T : Configurable<T>, new()
    {
        public static JsonSerializerOptions SerializerOptions { get; } = new()
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true
        };

        [Browsable(false)]
        public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

        [JsonIgnore, Browsable(false)]
        public string? FilePath { get; private set; }

        [JsonIgnore, Browsable(false)]
        public string? ConfigName { get; private set; }

        [JsonIgnore, Browsable(false)]
        public Action? Save { get; private set; }

        [JsonIgnore, Browsable(false)]
        private FileSystemWatcher? ConfigWatcher { get; set; }

        private static readonly object ReadLock = new();

        public static T FromFile(string filePath, string configName) => ReadFrom(filePath, configName);

        public void DisposeEvents()
        {
            ConfigWatcher?.Dispose();
            ConfigurationUpdated = null;
        }

        private void Initialise(string filePath, string configName)
        {
            FilePath = filePath;
            ConfigName = configName;
            Save = () => File.WriteAllText(FilePath, JsonSerializer.Serialize((T)this, SerializerOptions));
            ConfigWatcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath)) { EnableRaisingEvents = true };
            ConfigWatcher.Changed += (_, _) => Reload();
        }

        private void Reload()
        {
            lock (ReadLock)
            {
                T updated = Retry(() => ReadFrom(FilePath!, ConfigName!), 250, 2);
                updated.ConfigurationUpdated = ConfigurationUpdated;
                DisposeEvents();
                updated.ConfigurationUpdated?.Invoke(updated);
            }
        }

        private static T ReadFrom(string filePath, string configName)
        {
            T result = File.Exists(filePath)
                ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(filePath), SerializerOptions) ?? new T()
                : new T();
            result.Initialise(filePath, configName);
            return result;
        }

        private static TValue Retry<TValue>(Func<TValue> getValue, int timeout, int sleepTime) where TValue : new()
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeout)
            {
                try
                {
                    return getValue();
                }
                catch
                {
                    Thread.Sleep(sleepTime);
                }
            }

            throw new TimeoutException($"Configuration reload timed out after {timeout} ms.");
        }
    }

    public class ConfiguratorMixinBase
    {
        public virtual IUpdatableConfigurable[] MakeConfigurations(string configFolder) =>
            new IUpdatableConfigurable[] { Configurable<Config>.FromFile(Path.Combine(configFolder, "Config.json"), "Default Config") };

        public virtual bool TryRunCustomConfiguration(Configurator configurator) => false;
        public virtual void Migrate(string oldDirectory, string newDirectory) { }
    }

    public sealed class Configurator : IConfiguratorV3
    {
        private static readonly ConfiguratorMixin Mixin = new();
        private IUpdatableConfigurable[]? _configurations;

        public Configurator() { }
        public Configurator(string configDirectory) => ConfigFolder = configDirectory;

        public string? ModFolder { get; private set; }
        public string? ConfigFolder { get; private set; }
        public ConfiguratorContext Context { get; private set; }
        public IUpdatableConfigurable[] Configurations => _configurations ??= MakeConfigurations();

        private IUpdatableConfigurable[] MakeConfigurations()
        {
            var configurations = Mixin.MakeConfigurations(ConfigFolder!);
            for (int i = 0; i < configurations.Length; i++)
            {
                int index = i;
                configurations[i].ConfigurationUpdated += configurable => configurations[index] = configurable;
            }
            return configurations;
        }

        public T GetConfiguration<T>(int index) => (T)Configurations[index];
        public void SetConfigDirectory(string configDirectory) => ConfigFolder = configDirectory;
        public void SetContext(in ConfiguratorContext context) => Context = context;
        public IConfigurable[] GetConfigurations() => Configurations;
        public bool TryRunCustomConfiguration() => Mixin.TryRunCustomConfiguration(this);
        public void SetModDirectory(string modDirectory) => ModFolder = modDirectory;
        public void Migrate(string oldDirectory, string newDirectory) => Mixin.Migrate(oldDirectory, newDirectory);
    }
}
