using System.Security.Cryptography;
using System.Text.Json;

namespace DeskPanel.Agent;

/// <summary>
/// Phones must be approved once on the laptop (a Yes/No box). Approved phones get a random key they send on every
/// connect, so nobody else on the Wi-Fi can see the panel or answer Claude's permission requests.
/// </summary>
static class Pairing
{
    sealed record Phone(string Token, string Name, DateTime Added);
    sealed record Store(List<Phone> Phones);

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPanel", "phones.json");
    static readonly object Gate = new();
    static readonly SemaphoreSlim OneDialog = new(1, 1);

    public static bool IsKnown(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var given = System.Text.Encoding.UTF8.GetBytes(token);
        lock (Gate)
        {
            return Load().Phones.Any(p => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(p.Token), given));
        }
    }

    /// <summary>Asks on the laptop. Returns null if refused, unanswered, or the phone left (<paramref name="ct"/>).</summary>
    public static async Task<string?> Ask(string name, string address, CancellationToken ct)
    {
        try { if (!await OneDialog.WaitAsync(TimeSpan.FromMinutes(2), ct)) return null; }
        catch (OperationCanceledException) { return null; } // the phone gave up before its turn: no dialog
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                var r = MessageBox.Show(
                    $"A phone wants to connect to DeskPanel:\n\n{name}  ({address})\n\nAllow it? It will be able to see this laptop's stats, control media and volume, and answer Claude's permission requests.",
                    "DeskPanel – new phone", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
                tcs.TrySetResult(r == DialogResult.Yes);
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();

            bool ok;
            try { ok = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2), ct); }
            catch (TimeoutException) { ok = false; }
            catch (OperationCanceledException) { Log.Info($"phone {name} ({address}) left before it was approved"); return null; }
            if (!ok) { Log.Info($"phone {name} ({address}) was not allowed"); return null; }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            lock (Gate)
            {
                var store = Load();
                store.Phones.Add(new Phone(token, name, DateTime.Now));
                Save(store);
            }
            Log.Info($"phone {name} ({address}) paired");
            return token;
        }
        finally { OneDialog.Release(); }
    }

    public static void ForgetAll()
    {
        lock (Gate) Save(new Store([]));
        Log.Info("forgot all paired phones");
    }

    static Store Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store([]);
        }
        catch (Exception e) { Log.Error("phones.json", e); }
        return new Store([]);
    }

    static void Save(Store s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // write beside the target, then swap, so a crash mid-write cannot wipe the paired phones
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
