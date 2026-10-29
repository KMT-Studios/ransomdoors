using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace rans0m
{
    public static class CoinValues
    {
        public static readonly Dictionary<int, int> ExtensionValues = new()
        {
            { 1, 10 },
            { 2, 50 },
            { 3, 100 },
            { 4, 150 },
            { 5, 200 },
            { 6, 500 } // Honeypot
        };

        public static int GetWeightedExtension()
        {
            int roll = Global.Rng.Next(100);
            return roll switch
            {
                < 40 => 1,  // 40% chance
                < 70 => 2,  // 30% chance
                < 85 => 3,  // 15% chance
                < 93 => 4,  // 8% chance
                _ => 5      // 7% chance
            };
        }

        public static int GetValue(int extension) => ExtensionValues.GetValueOrDefault(extension, 10);
    }

    public static class GoldCoinManager
    {
        private const string RegistryPath = @"Software\RANSOM";
        private const string RegistryValueName = "GoldCoins";
        private const string DirRegistryValueName = "GoldDirectories";

        // Should make this configurable
        private static List<string> targetFolders = new List<string>()
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        public static string temporaryFolderPath = Path.Combine(Path.GetTempPath(), "RansomDrawers");
        private const double CrucifixSpawnChance = 0.1;
        private const double Gold6SpawnChance = 0.3;

        // The drawer root actually used by the current run. Normally the shared temporaryFolderPath,
        // but a stale folder can be locked by a file manager, in which case we fall back to a
        // uniquely named root so a leftover handle cannot abort coin generation.
        private static string activeDrawerRoot = temporaryFolderPath;

        /// <summary>
        /// Generate coins, chooses method based on config
        /// </summary>
        public static int GenerateCoins()
        {
            return Config.Current.UseDrawerMode ? GenerateFolderDrawerCoins() : GenerateScatteredCoins();
        }

        // ----------------- SCATTERED MODE -----------------

        /// <summary>
        /// Scatters coins randomly across user folders
        /// </summary>
        private static int GenerateScatteredCoins()
        {
            int targetGold = (int)(Config.Current.RansomAmount * 1.2);
            int generatedGold = 0;
            List<string> createdPaths = new List<string>();
            HashSet<string> touchedDirs = new HashSet<string>();

            // Gold Coins
            int maxAttempts = 500;
            int attempts = 0;
            while (generatedGold < targetGold && attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    string baseDir = targetFolders[Global.Rng.Next(targetFolders.Count)];
                    if (!Directory.Exists(baseDir)) continue;

                    string targetDir = GetRandomLocationInTree(baseDir);
                    Directory.CreateDirectory(targetDir);

                    // Never track the base folders themselves for deletion, only their subfolders
                    if (!targetFolders.Contains(targetDir, StringComparer.OrdinalIgnoreCase))
                        touchedDirs.Add(targetDir);

                    int extension = CoinValues.GetWeightedExtension(); // 1-5 only
                    int coinValue = CoinValues.GetValue(extension);

                    if (CreateCoinFile(targetDir, extension) is { } coinFile)
                    {
                        createdPaths.Add(coinFile);
                        generatedGold += coinValue;
                    }
                }
                catch {}
            }

            // Honeypot
            if (Global.Rng.NextDouble() < Gold6SpawnChance)
            {
                try
                {
                    string baseDir = targetFolders[Global.Rng.Next(targetFolders.Count)];
                    if (Directory.Exists(baseDir))
                    {
                        string targetDir = GetRandomLocationInTree(baseDir);
                        Directory.CreateDirectory(targetDir);
                        if (!targetFolders.Contains(targetDir, StringComparer.OrdinalIgnoreCase))
                            touchedDirs.Add(targetDir);

                        if (CreateCoinFile(targetDir, 6) is { } coinFile)
                            createdPaths.Add(coinFile);
                    }
                }
                catch { }
            }

            // Crucifix
            if (Global.Rng.NextDouble() < CrucifixSpawnChance)
            {
                try
                {
                    string baseDir = targetFolders[Global.Rng.Next(targetFolders.Count)];
                    if (Directory.Exists(baseDir))
                    {
                        string targetDir = GetRandomLocationInTree(baseDir);
                        Directory.CreateDirectory(targetDir);
                        if (!targetFolders.Contains(targetDir, StringComparer.OrdinalIgnoreCase))
                            touchedDirs.Add(targetDir);

                        if (CreateCrucifixFile(targetDir) is { } crucifixFile)
                            createdPaths.Add(crucifixFile);
                    }
                }
                catch { }
            }

            AppendToRegistry(createdPaths);
            AppendDirsToRegistry(touchedDirs);
            return generatedGold;
        }

        private static int GetBalancedMaxSubfolderDepth() => Math.Clamp(2 + Config.Current.InfectionDuration / 45, 2, 8);

        /// <returns>A random location in the folder tree, starting from the root</returns>
        private static string GetRandomLocationInTree(string root)
        {
            List<string> chain = new List<string> { root };
            int depth = Global.Rng.Next(GetBalancedMaxSubfolderDepth() + 1);

            string current = root;
            for (int i = 0; i < depth; i++)
            {
                try
                {
                    string[] subDirs = Directory.GetDirectories(current);
                    if (subDirs.Length == 0) break;
                    current = subDirs[Global.Rng.Next(subDirs.Length)];
                    chain.Add(current);
                }
                catch { break; }
            }

            return chain[Global.Rng.Next(chain.Count)];
        }

        // ---------------- FOLDER DRAWER MODE ----------------

        private const double AverageCoinValue = 60;


        private static (int drawerCount, int itemsPerDrawer) GetBalancedDrawerLayout()
        {
            int targetGold = (int)(Config.Current.RansomAmount * 1.2);

            int slotsForGold = (int)Math.Ceiling(targetGold / AverageCoinValue * 1.5);
            int slotsForDuration = Config.Current.InfectionDuration / 2;

            int totalSlots = Math.Max(slotsForGold, slotsForDuration);

            int itemsPerDrawer = Math.Clamp((int)Math.Round(Math.Sqrt(totalSlots)), 3, 8);
            int drawerCount = Math.Max(1, (int)Math.Ceiling((double)totalSlots / itemsPerDrawer));

            return (drawerCount, itemsPerDrawer);
        }

        private static string GenerateRandomFolderName() => Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Clears out any drawers left by a previous run and returns the folder to build this run in.
        /// A leftover Explorer or file-manager window can keep the shared root locked, which used to
        /// make the recursive delete throw and abort generation with zero coins, so a locked root is
        /// abandoned in favour of a uniquely named one rather than failing the encounter.
        /// </summary>
        private static string PrepareDrawerRoot()
        {
            CloseDrawerExplorerWindow();

            try
            {
                if (Directory.Exists(temporaryFolderPath))
                    Directory.Delete(temporaryFolderPath, true);

                Directory.CreateDirectory(temporaryFolderPath);
                activeDrawerRoot = temporaryFolderPath;
                return activeDrawerRoot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[GoldCoinManager] Could not reuse drawer root: {ex.Message}");
            }

            string fallback = Path.Combine(Path.GetTempPath(), $"RansomDrawers-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallback);
            activeDrawerRoot = fallback;
            return activeDrawerRoot;
        }

        /// <summary>
        /// Creates a temporary folder structure
        /// </summary>
        private static int GenerateFolderDrawerCoins()
        {
            int targetGold = (int)(Config.Current.RansomAmount * 1.2);
            int generatedGold = 0;
            List<string> createdPaths = new List<string>();
            List<string> allDirs = new List<string>(); // Every drawer/item/subfolder dir made this pass

            try
            {
                string drawerRoot = PrepareDrawerRoot();

                (int drawerCount, int itemsPerDrawer) = GetBalancedDrawerLayout();

                // Coins and drawers
                for (int drawer = 0; drawer < drawerCount && generatedGold < targetGold; drawer++)
                {
                    string drawerPath = Path.Combine(drawerRoot, GenerateRandomFolderName());
                    Directory.CreateDirectory(drawerPath);
                    allDirs.Add(drawerPath);

                    for (int item = 0; item < itemsPerDrawer && generatedGold < targetGold; item++)
                    {
                        string itemPath = Path.Combine(drawerPath, GenerateRandomFolderName());
                        Directory.CreateDirectory(itemPath);

                        List<string> pathLevels = new List<string> { drawerPath, itemPath };

                        // Randomly add more nesting (50% chance)
                        if (Global.Rng.Next(100) < 50)
                        {
                            string subfolderPath = Path.Combine(itemPath, GenerateRandomFolderName());
                            Directory.CreateDirectory(subfolderPath);
                            pathLevels.Add(subfolderPath);
                        }

                        allDirs.AddRange(pathLevels.Skip(1)); // drawerPath already added above

                        string coinDir = pathLevels[Global.Rng.Next(pathLevels.Count)];

                        int extension = CoinValues.GetWeightedExtension();
                        int coinValue = CoinValues.GetValue(extension);

                        if (CreateCoinFile(coinDir, extension) is { } coinFile)
                        {
                            createdPaths.Add(coinFile);
                            generatedGold += coinValue;
                        }
                    }
                }

                // Honeypot
                if (allDirs.Count > 0 && Global.Rng.NextDouble() < Gold6SpawnChance)
                {
                    try
                    {
                        string dir = allDirs[Global.Rng.Next(allDirs.Count)];
                        if (CreateCoinFile(dir, 6) is { } coinFile)
                            createdPaths.Add(coinFile);
                    }
                    catch { }
                }

                // Crucifix
                if (allDirs.Count > 0 && Global.Rng.NextDouble() < CrucifixSpawnChance)
                {
                    try
                    {
                        string dir = allDirs[Global.Rng.Next(allDirs.Count)];
                        if (CreateCrucifixFile(dir) is { } crucifixFile)
                            createdPaths.Add(crucifixFile);
                    }
                    catch { }
                }

                AppendToRegistry(createdPaths);

                System.Diagnostics.Process.Start("explorer.exe", activeDrawerRoot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GoldCoinManager] Drawer generation failed: {ex}");
            }

            return generatedGold;
        }

        /// <summary>
        /// Closes any Explorer window showing the drawer folder
        /// </summary>
        private static void CloseDrawerExplorerWindow()
        {
            try
            {
                Thread thread = new Thread(() =>
                {
                    try
                    {
                        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                        if (shellType is null)
                        {
                            return;
                        }

                        dynamic? shell = Activator.CreateInstance(shellType);
                        if (shell is null)
                        {
                            return;
                        }

                        foreach (dynamic window in shell.Windows())
                        {
                            try
                            {
                                string? url = window.LocationURL as string;
                                if (string.IsNullOrWhiteSpace(url))
                                {
                                    continue;
                                }

                                string path = Uri.UnescapeDataString(new Uri(url).LocalPath);
                                if (path.StartsWith(activeDrawerRoot, StringComparison.OrdinalIgnoreCase))
                                {
                                    window.Quit();
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(2000);
            }
            catch
            {
            }
        }

        // ------------------- COMMON HELPERS -------------------

        /// <summary>
        /// Creates a single encrypted coin file and returns its path
        /// </summary>
        private static string? CreateCoinFile(string directory, int extension)
        {
            try
            {
                // Create payload
                Dictionary<string, string> payload = new Dictionary<string, string>
                {
                    { "RANSOM_COIN", Guid.NewGuid().ToString("N") }
                };

                string json = JsonSerializer.Serialize(payload);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                // Encrypt
                byte[] encrypted = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);

                // Generate file with random name and extension
                string fileName = $"{Guid.NewGuid():N}.gold{extension}";
                string fullPath = Path.Combine(directory, fileName);

                File.WriteAllBytes(fullPath, encrypted);
                return fullPath;
            }
            catch { return null; }
        }

        private static string? CreateCrucifixFile(string directory)
        {
            try
            {
                Dictionary<string, string> payload = new Dictionary<string, string>
                {
                    { "RANSOM_COIN", Guid.NewGuid().ToString("N") }
                };

                string json = JsonSerializer.Serialize(payload);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                byte[] encrypted = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);

                string fileName = $"{Guid.NewGuid():N}.crucifix";
                string fullPath = Path.Combine(directory, fileName);

                File.WriteAllBytes(fullPath, encrypted);
                return fullPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Decrypts a coin file and returns its contents
        /// </summary>
        public static Dictionary<string, string>? DecryptCoinFile(string filePath)
        {
            try
            {
                byte[] encrypted = File.ReadAllBytes(filePath);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(decrypted);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch { return null; }
        }

        /// <summary>
        /// Deletes all coins and cleans up
        /// </summary>
        public static void DeleteAllCoins()
        {
            List<string>? paths = GetRegistryFileList();
            if (paths != null)
            {
                foreach (string path in paths)
                {
                    try { File.Delete(path); }
                    catch { }
                }
            }

            // Deepest path first
            List<string>? dirs = GetRegistryDirList();
            if (dirs != null)
            {
                foreach (string dir in dirs.OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir); }
                    catch { }
                }
            }

            // Close any Explorer window drawer mode opened for the temp folder before deleting it.
            if (Config.Current.UseDrawerMode)
                CloseDrawerExplorerWindow();

            // Clean up the folder drawer mode built this run. It may be the shared root or a
            // uniquely named fallback, and it may be held open by another app, so this is best
            // effort: a locked folder only lingers until the holding process exits.
            foreach (string root in new[] { activeDrawerRoot, temporaryFolderPath })
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GoldCoinManager] Could not delete drawer root {root}: {ex.Message}");
                }
            }

            activeDrawerRoot = temporaryFolderPath;

            // Clear registry
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    key?.DeleteValue(RegistryValueName, false);
                    key?.DeleteValue(DirRegistryValueName, false);
                }
            }
            catch { }
        }

        // --------------- REGISTRY HELPERS ---------------

        private static void AppendToRegistry(List<string> newPaths)
        {
            List<string> existing = GetRegistryFileList() ?? new List<string>();
            existing.AddRange(newPaths);
            SetRegistryFileList(existing);
        }

        private static List<string>? GetRegistryFileList()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    object? value = key?.GetValue(RegistryValueName);
                    return value is string[] arr ? new List<string>(arr) : null;
                }
            }
            catch { return null; }
        }

        private static void SetRegistryFileList(List<string> paths)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    key?.SetValue(RegistryValueName, paths.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch { }
        }

        private static void AppendDirsToRegistry(IEnumerable<string> newDirs)
        {
            List<string> existing = GetRegistryDirList() ?? new List<string>();
            existing.AddRange(newDirs);
            SetRegistryDirList(existing);
        }

        private static List<string>? GetRegistryDirList()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    object? value = key?.GetValue(DirRegistryValueName);
                    return value is string[] arr ? new List<string>(arr) : null;
                }
            }
            catch { return null; }
        }

        private static void SetRegistryDirList(List<string> dirs)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    key?.SetValue(DirRegistryValueName, dirs.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch { }
        }
    }
}