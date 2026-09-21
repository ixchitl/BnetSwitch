#!/usr/bin/env bash
set -euo pipefail
repo_dir=$(cd "$(dirname "$0")/.." && pwd)
test_dir=$(mktemp -d)
# XDG directories isolate the real production file APIs on Linux; never run on Windows.
export XDG_DATA_HOME="$test_dir/local"
export XDG_CONFIG_HOME="$test_dir/roaming"
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
cat > "$test_dir/Check.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>
<Compile Include="$repo_dir/Services/AppSettings.cs" />
<Compile Include="$repo_dir/Services/BattleNetPaths.cs" />
<Compile Include="$repo_dir/Services/AppDataStore.cs" />
<Compile Include="$repo_dir/Services/AccountReader.cs" />
<Compile Include="$repo_dir/Models/BattleAccount.cs" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.7" />
</ItemGroup>
</Project>
PROJECT
cat > "$test_dir/Program.cs" <<'CS'
using BnetSwitch.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;
if (!OperatingSystem.IsLinux()) throw new Exception("Linux isolation required");
var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (local != Environment.GetEnvironmentVariable("XDG_DATA_HOME")) throw new Exception("Isolation failed");
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
var fresh = AppSettings.Load();
Check(fresh.CloseToTray && !fresh.ForceKillAgentOnSwitch, "fresh settings defaults");
var settingsPath = Path.Combine(local, "BnetSwitch", "settings.json");
File.WriteAllText(settingsPath, """
{"DarkMode":true,"AccountNotes":{"42":"synthetic note"},"PinnedAccountIds":[42],"HiddenAccountIds":[43],"ClientExe":"synthetic.exe","ApiBaseUrl":"http://127.0.0.1:9","UpdateUrl":"http://127.0.0.1:9/version","LicenseCode":"synthetic","AdFreeCached":false,"SplashAd":{"Enabled":true,"ImageUrl":"http://127.0.0.1:9/ad"},"BottomAd":{"Enabled":true},"GithubUrl":"http://127.0.0.1:9/old"}
""");
var old = AppSettings.Load();
Check(old.DarkMode && old.AccountNotes["42"] == "synthetic note" && old.PinnedAccountIds.SequenceEqual(new long[]{42}) && old.HiddenAccountIds.SequenceEqual(new long[]{43}) && old.ClientExe == "synthetic.exe", "legacy local preferences retained");
using (var normalized = JsonDocument.Parse(File.ReadAllText(settingsPath)))
    Check(new[]{"ApiBaseUrl","UpdateUrl","LicenseCode","AdFreeCached","SplashAd","BottomAd","GithubUrl"}.All(k => !normalized.RootElement.TryGetProperty(k, out _)), "legacy service configuration discarded");
File.WriteAllText(settingsPath, "{broken");
Check(AppSettings.Load().CloseToTray, "corrupt settings recover");
var paths = new BattleNetPaths();
Directory.CreateDirectory(paths.LocalRoot);
Directory.CreateDirectory(paths.RoamingDir);
using (var conn = new SqliteConnection("Data Source=" + paths.CachedDataDb))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
    CREATE TABLE login_cache(name TEXT,environment TEXT,battle_tag TEXT,account_id_lo INTEGER);
    CREATE TABLE key_value_store(key TEXT,value TEXT);
    INSERT INTO login_cache VALUES('synthetic','cn','Synthetic#1234',42);
    INSERT INTO key_value_store VALUES('features_cached_data_points','{"account_id":42}');
    """;
    cmd.ExecuteNonQuery();
    var reader = new AccountReader(paths);
    var accounts = reader.ReadAccounts(out var active);
    Check(accounts.Count == 1 && accounts[0].AccountId == 42 && active == 42 && accounts[0].ConnectedEnvironments == "", "legacy SQLite accounts without region column");
    cmd.CommandText = "ALTER TABLE login_cache ADD COLUMN connected_environments TEXT; UPDATE login_cache SET connected_environments='cn';";
    cmd.ExecuteNonQuery();
    Check(reader.ReadAccounts(out _)[0].ConnectedEnvironments == "cn", "current SQLite region retained");
}
var store = new AppDataStore(paths);
var legacyDir = Path.Combine(store.Root, "42");
Directory.CreateDirectory(Path.Combine(legacyDir, "BattleNet"));
File.WriteAllText(Path.Combine(legacyDir, "meta.json"), """{"AccountId":42,"BattleTag":"Synthetic#1234","SavedAtUtc":"2026-01-01T00:00:00Z"}""");
const string config = """{"Client":{"SavedAccountNames":"synthetic@example.invalid"}}""";
File.WriteAllText(Path.Combine(legacyDir, "BattleNet", "Battle.net.config"), config);
File.WriteAllText(Path.Combine(legacyDir, "cacheddata_pointer.json"), """{"account_id":42}""");
File.WriteAllText(Path.Combine(legacyDir, "uauth.json"), """{"synthetic":"AQID"}""");
File.WriteAllText(Path.Combine(legacyDir, "uauth_slot.txt"), "synthetic");
Check(store.HasProfile(42) && store.ReadMeta(42)?.Expired == false && store.ReadLoginName(42) == "synthetic@example.invalid", "legacy snapshot metadata and login name");
Check(store.ReadTokens(42)["synthetic"].SequenceEqual(new byte[]{1,2,3}) && store.ReadOwnSlot(42) == "synthetic" && store.ReadPointer(42) == """{"account_id":42}""", "synthetic snapshot token and pointer format");
store.Restore(42);
Check(File.ReadAllText(paths.RoamingConfig) == config, "legacy snapshot file restore");
store.Save(44, "Synthetic#5678");
Check(store.ReadMeta(44)?.BattleTag == "Synthetic#5678" && store.ReadLoginName(44) == "synthetic@example.invalid", "snapshot save round trip");
Console.WriteLine("Synthetic checks only; no Windows GUI, registry, process control or real login exercised.");
CS
"${DOTNET:-$HOME/.dotnet/dotnet}" run --project "$test_dir/Check.csproj" --nologo
