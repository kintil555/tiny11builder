using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class CustomizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "t11-cust-" + Guid.NewGuid().ToString("N"));
    public CustomizationTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Finds_a_7z_payload_appended_to_an_installer_stub_across_buffer_boundaries()
    {
        var file = Path.Combine(root, "setup.exe");
        var stub = new byte[(1 << 20) - 3];
        File.WriteAllBytes(file, [.. stub, .. ValidHeader()]);
        Assert.Equal(stub.Length, DriverSource.FindSignature(file));
        File.WriteAllBytes(file, stub);
        Assert.Null(DriverSource.FindSignature(file));
    }

    [Fact]
    public void Bare_signature_inside_a_stub_is_skipped_in_favour_of_the_real_archive()
    {
        var file = Path.Combine(root, "sfx.exe");
        byte[] fake = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 9, 9, 9, 9];
        File.WriteAllBytes(file, [.. fake, .. new byte[100], .. ValidHeader()]);
        Assert.Equal(fake.Length + 100, DriverSource.FindSignature(file));
    }

    private static byte[] ValidHeader()
    {
        var h = new byte[32];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(h, 0);
        BitConverter.GetBytes(DriverSource.Crc32(h.AsSpan(12, 20))).CopyTo(h, 8);
        return h;
    }

    [Fact]
    public void GHelper_requires_an_existing_exe_and_enables_post_install()
    {
        Assert.Throws<InvalidDataException>(() => PostInstall.Validate(new BuildRecipe { InstallGHelper = true, GHelperPath = Path.Combine(root, "none.exe") }));
        var exe = Path.Combine(root, "GHelper.exe");
        File.WriteAllBytes(exe, [1]);
        var recipe = new BuildRecipe { InstallGHelper = true, GHelperPath = exe };
        PostInstall.Validate(recipe);
        Assert.True(PostInstall.Enabled(recipe));
    }

    [Fact]
    public void Junk_cleaner_is_embedded_and_enables_post_install()
    {
        Assert.Contains("JUNK FILES CLEANER", PostInstall.ReadJunkCleaner());
        Assert.True(PostInstall.Enabled(new BuildRecipe { IncludeJunkCleaner = true }));
    }

    [Fact]
    public void Folder_and_inf_sources_resolve_to_their_driver_folder_and_empty_folders_are_rejected()
    {
        var drivers = Path.Combine(root, "lan");
        Directory.CreateDirectory(drivers);
        File.WriteAllText(Path.Combine(drivers, "net.inf"), "; test");
        var found = DriverSource.Prepare([drivers, Path.Combine(drivers, "net.inf")], Path.Combine(root, "scratch"), _ => { }, default);
        Assert.Single(found);
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<InvalidDataException>(() => DriverSource.Prepare([empty], root, _ => { }, default));
        Assert.Throws<InvalidDataException>(() => DriverSource.Validate(Path.Combine(root, "missing.exe")));
    }

    [Theory]
    [InlineData("Brave.Brave", true)]
    [InlineData("7zip.7zip", true)]
    [InlineData("bad id; calc", false)]
    [InlineData("", false)]
    public void Winget_ids_are_validated(string id, bool valid)
    {
        var recipe = new BuildRecipe { PostInstallApps = [id] };
        if (valid) PostInstall.Validate(recipe);
        else Assert.Throws<InvalidDataException>(() => PostInstall.Validate(recipe));
    }
}
