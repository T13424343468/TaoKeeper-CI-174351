using System.Security.Cryptography;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Services;

// TaoKeeper CI data-effect probe: seed a known tree at real scale, run the real
// engines, assert every number exactly. Exit 0 = effect matches expectation.
var seedRoot = Path.Combine(Path.GetTempPath(), $"tk-effect-{DateTime.Now:HHmmss}");
const int KeepCount = 500;
const int JunkCount = 1200;
long junkBytes = 0, totalBytes = 0;
long dupReclaim = 8192L + 65536L * 2 + 16384L; // g1: 8K x2, g2: 64K x3, g3: 16K x2

try
{
    Console.WriteLine($"[SEED] root={seedRoot}");
    for (var i = 0; i < KeepCount; i++)
    {
        var p = Path.Combine(seedRoot, "keep", $"keep_{i:D4}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = System.Text.Encoding.UTF8.GetBytes($"keep-unique-{i}-{$"{(char)('A' + i % 26)}"}-{i * 7919}");
        File.WriteAllBytes(p, data);
        totalBytes += data.LongLength;
    }
    for (var i = 0; i < JunkCount; i++)
    {
        var size = 4096L + (i % 16) * 4096L; // 4KB..64KB deterministic
        var p = Path.Combine(seedRoot, "junk", $"junk_{i:D4}.tmpjunk");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        using (var f = File.Create(p))
        {
            // unique per-file content: index-stamped blocks (never all-zero)
            var head = new byte[1024];
            System.Text.Encoding.UTF8.GetBytes($"junk-unique-{i:D4}-").AsSpan().CopyTo(head);
            for (var w = 0; w < size; w += 1024)
            {
                BitConverter.GetBytes((long)i * 104729 + w).AsSpan().CopyTo(head.AsSpan(1000, 8));
                f.Write(head, 0, (int)Math.Min(1024, size - w));
            }
        }
        junkBytes += size;
        totalBytes += size;
    }
    var dupSets = new (int files, long size)[] { (2, 8192), (3, 65536), (2, 16384) };
    for (var g = 0; g < dupSets.Length; g++)
    {
        var (n, sz) = dupSets[g];
        var data = new byte[sz];
        RandomNumberGenerator.Fill(data);
        for (var k = 0; k < n; k++)
        {
            var p = Path.Combine(seedRoot, "dup", $"g{g}", $"copy_{k}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            totalBytes += sz;
        }
    }
    var totalFiles = KeepCount + JunkCount + 7;
    Console.WriteLine($"[SEED] files={totalFiles} junk={JunkCount} junkBytes={junkBytes} totalBytes={totalBytes}");

    // 1) Real disk scan with hashing
    var scanner = new PortableDiskScanner(new Sha256FileHasher());
    var scan = await scanner.ScanAsync(new[] { seedRoot }, new ScanOptions { ComputeHashes = true });
    var scanFiles = scan.Files.ToList();
    var scanBytes = scanFiles.Sum(f => f.SizeBytes);
    Console.WriteLine($"[SCAN] files={scanFiles.Count} bytes={scanBytes}");
    if (scanFiles.Count != totalFiles) throw new Exception($"scan file count {scanFiles.Count} != {totalFiles}");
    if (scanBytes != totalBytes) throw new Exception($"scan bytes {scanBytes} != {totalBytes}");

    // 2) Real duplicate finder on scanned files
    var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
    var groups = await finder.FindAsync(scan.Files);
    var reclaim = groups.Sum(g => g.ReclaimableBytes);
    foreach (var g in groups) Console.WriteLine($"[DUP] group {g.Files.Count} files, size={g.SizeBytes}, reclaimable={g.ReclaimableBytes}");
    if (groups.Count != 3) throw new Exception($"dup groups {groups.Count} != 3");
    if (reclaim != dupReclaim) throw new Exception($"dup reclaim {reclaim} != {dupReclaim}");

    // 3) Real junk rule engine with one probe rule (pattern .tmpjunk)
    var rule = new JunkRule("probe-junk", "探针垃圾", "CI 效果核验规则", new[] { seedRoot }, "*.tmpjunk", null, JunkRisk.Low);
    var engine = new JunkRuleEngine(new[] { rule });
    var junk = await engine.ScanAsync(_ => null, new HashSet<string>(), rootsOverride: new[] { seedRoot });
    Console.WriteLine($"[JUNK] count={junk.TotalCount} bytes={junk.TotalBytes}");
    if (junk.TotalCount != JunkCount) throw new Exception($"junk count {junk.TotalCount} != {JunkCount}");
    if (junk.TotalBytes != junkBytes) throw new Exception($"junk bytes {junk.TotalBytes} != {junkBytes}");

    Console.WriteLine("[OK] data effect probe: every number exact on real FS, real engines");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] {ex.Message}");
    return 2;
}
finally
{
    try { if (Directory.Exists(seedRoot)) Directory.Delete(seedRoot, true); } catch { }
}
