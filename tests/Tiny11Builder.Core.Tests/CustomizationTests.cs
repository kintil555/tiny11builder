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
        File.WriteAllBytes(file, [.. stub, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3]);
        Assert.Equal(stub.Length, DriverSource.FindSignature(file));
        File.WriteAllBytes(file, stub);
        Assert.Null(DriverSource.FindSignature(file));
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
