using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityModManagerNet
{
	public partial class UnityModManager
	{
		private static readonly Version VER_0 = new Version();

		private static readonly Version VER_0_13 = new Version(0, 13);

		public static readonly List<ModEntry> modEntries = new List<ModEntry>();

		internal static bool started;
		internal static bool initialized;

		/// <summary>
		///     Contains version of UnityEngine
		/// </summary>
		public static Version unityVersion { get; private set; }

		/// <summary>
		///     Contains version of a game, if configured [0.15.0]
		/// </summary>
		public static Version gameVersion { get; private set; } = new Version();

		public static Version version { get; } = typeof(UnityModManager).Assembly.GetName().Version;
		public static string modsPath { get; private set; }

		internal static Param Params { get; set; } = new Param();
		internal static GameInfo Config { get; set; } = new GameInfo();

		public static bool Initialize()
		{
			if (initialized)
				return true;

			initialized = true;

			Logger.Clear();

			Logger.Log($"Initialize. Version '{version}'.");

			unityVersion = ParseVersion(Application.unityVersion);

			Config = GameInfo.Load();
			if (Config == null)
				return false;

			Params = Param.Load();

			modsPath = Path.Combine(Environment.CurrentDirectory, Config.ModsDirectory);

			if (!Directory.Exists(modsPath))
				Directory.CreateDirectory(modsPath);

			AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

			return true;
		}

		private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
			if (assembly != null)
				return assembly;

			var assName = new AssemblyName(args.Name);

			if (!assName.Name.StartsWith("0Harmony", StringComparison.InvariantCultureIgnoreCase))
				return null;

			string filepath = Path.Combine(UMMLoader.UMMLoader.BinariesPath, $"0Harmony-{assName.Version.Major}.{assName.Version.Minor}.dll");

			if (!File.Exists(filepath))
				return null;

			try
			{
				return Assembly.LoadFile(filepath);
			}
			catch (Exception e)
			{
				Logger.Error(e.ToString());
			}

			return null;
		}

		public static void Start()
		{
			try
			{
				_Start();
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				OpenUnityFileLog();
			}
		}

		private static void ParseGameVersion()
		{
			if (string.IsNullOrEmpty(Config.GameVersionPoint))
				return;
			try
			{
				Logger.Log("Start parsing game version.");
				if (!Injector.TryParseEntryPoint(Config.GameVersionPoint, out string assembly, out string className, out string methodName, out _))
					return;
				var asm = Assembly.Load(assembly);
				if (asm == null)
				{
					Logger.Error($"File '{assembly}' not found.");
					return;
				}

				var foundClass = asm.GetType(className);
				if (foundClass == null)
				{
					Logger.Error($"Class '{className}' not found.");
					return;
				}

				var foundMethod = foundClass.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (foundMethod == null)
				{
					var foundField = foundClass.GetField(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					if (foundField != null)
					{
						gameVersion = ParseVersion(foundField.GetValue(null).ToString());
						Logger.Log($"Game version detected as '{gameVersion}'.");
						return;
					}

					Logger.Error($"Method '{methodName}' not found.");
					return;
				}

				gameVersion = ParseVersion(foundMethod.Invoke(null, null).ToString());
				Logger.Log($"Game version detected as '{gameVersion}'.");
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				OpenUnityFileLog();
			}
		}

		private static void _Start()
		{
			if (!Initialize())
			{
				Logger.Log("Cancel start due to an error.");
				OpenUnityFileLog();
				return;
			}

			if (started)
			{
				Logger.Log("Cancel start. Already started.");
				return;
			}

			started = true;

			ParseGameVersion();

			if (Directory.Exists(modsPath))
                LoadMods(modsPath);

			if (!UI.Load())
				Logger.Error("Can't load UI.");
		}

        private static void LoadMods(string modsFolder)
        {
            Logger.Log($"Parsing mods from mods folder ({modsFolder}).");

            //single-depth dir search
            BuildIdToModMap(modsFolder, out var totalModCount, out var idToModMap);

            if (idToModMap.Count > 0)
            {
                Logger.Log("Sorting mods.");
                AddAllModsInDependencyOrder(idToModMap, outputList: modEntries);

                Params.ReadModParams();

                Logger.Log("Loading mods.");
                foreach (var mod in modEntries)
                    if (!mod.Enabled)
                        mod.Logger.Log("To skip (disabled).");
                    else
                        //the setter on this is deceptively heavy;
                        //the mod load logic happens once its property is made active. 
                        mod.Active = true; 
            }

            int successfulModLoadCount = modEntries.Count(x => !x.ErrorOnLoading);
            Logger.Log($"Finish. Found {totalModCount} mods. Successful loaded {successfulModLoadCount} mods.\n\n".ToUpper());
        }

        private static void BuildIdToModMap(string modsFolder, out int totalModCount, out Dictionary<string, ModEntry> idToModMap)
        {
            idToModMap = new Dictionary<string, ModEntry>();
            totalModCount = 0;
            
            foreach (string dir in Directory.GetDirectories(modsFolder))
            {
                string jsonPath = Path.Combine(dir, Config.ModInfo);
                if (!File.Exists(Path.Combine(dir, Config.ModInfo)))
                    jsonPath = Path.Combine(dir, Config.ModInfo.ToLower());

                if (!File.Exists(jsonPath))
                    continue;

                totalModCount++;
                Logger.Log($"Reading file '{jsonPath}'.");
                try
                {
                    var modInfo = JsonUtility.FromJson<ModInfo>(File.ReadAllText(jsonPath));
                    if (string.IsNullOrEmpty(modInfo.Id))
                    {
                        Logger.Error("Id is null.");
                        continue;
                    }

                    if (idToModMap.ContainsKey(modInfo.Id))
                    {
                        Logger.Error($"Id '{modInfo.Id}' already uses another mod.");
                        continue;
                    }

                    if (string.IsNullOrEmpty(modInfo.AssemblyName))
                        modInfo.AssemblyName = modInfo.Id + ".dll";

                    Logger.Log("modInfo shape: " + modInfo);
                    var modEntry = new ModEntry(modInfo, dir + Path.DirectorySeparatorChar);
                    idToModMap.Add(modInfo.Id, modEntry);
                }
                catch (Exception exception)
                {
                    Logger.Error($"Error parsing file '{jsonPath}'.");
                    Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// Produces a list of the mods values, sorted in "dependency order"
        /// such that every mod is loaded sometime after all of the mods it depends on.
        /// </summary>
        /// <param name="idToModMap">Input map/dict mapping mods to their unique ids.</param>
        /// <param name="outputList">
        /// Optional pre-existing list to add the mods to (to reduce potential allocations).
        /// If not supplied, a new list will be created.
        /// </param>
        /// <returns>
        /// A list of mods, sorted in dependency order. If an extant list was supplied via <see cref="outputList"/>,
        /// the <see cref="outputList"/> will be returned, now with the mods added.
        /// </returns>
        private static List<ModEntry> AddAllModsInDependencyOrder(
            Dictionary<string, ModEntry> idToModMap,
            List<ModEntry> outputList = null)
        {
            if (outputList == null) outputList = new List<ModEntry>(); //if one wasn't supplied to re-use, create a new one.
            
			foreach (string id in idToModMap.Keys)
				AddModPlusDependenciesFromMap(id, idToModMap, outputList);
            
            return outputList;
		}

        /// <summary>
        /// Adds the mod with the given id from the given map, to the given list, in dependency order.
        /// Ensures all of the mod's dependencies are listed before the mod itself.
        /// Since those dependencies may themselves have dependencies (and so on), this method is used recursively.
        /// </summary>
        /// <param name="id">id of the ModEntry to add to the destination list (preceded by its dependencies, which may in turn have dependencies)</param>
        /// <param name="idToModMap">Dictionary lookup table for mapping each mod id to its respective <see cref="ModEntry"/>.</param>
        /// <param name="outputList">The list that mods will be added to</param>
        private static void AddModPlusDependenciesFromMap(string id, Dictionary<string, ModEntry> idToModMap, List<ModEntry> outputList)
        {
            //TODO: Guard against cyclic dependencies
            //TODO: consider using a HashSet cache for better performance on larger mod rigs
            //  (though you'll need hundreds, maybe thousands before this really make a difference). 
            //  on the other end of that, smaller mod rigs won't be noticeably harmed by a hashset cache. 
            if (outputList.Any(m => m.Info.Id == id)) 
                return;
            
            //intent: load requirements first, then the mod itself
            foreach (string req in idToModMap[id].Requirements.Keys)
                AddModPlusDependenciesFromMap(req, idToModMap, outputList);
            outputList.Add(idToModMap[id]);
        }

        public static ModEntry FindMod(string id) { return modEntries.FirstOrDefault(x => x.Info.Id == id); }

		public static Version GetVersion() { return version; }

		public static void SaveSettingsAndParams()
		{
			Params.Save();
			foreach (var mod in modEntries)
				if (mod.Active && mod.OnSaveGUI != null)
					try
					{
						mod.OnSaveGUI(mod);
					}
					catch (Exception e)
					{
						mod.Logger.Error($"OnSaveGUI: {e.GetType().Name} - {e.Message}");
						Debug.LogException(e);
					}
		}
	}
}