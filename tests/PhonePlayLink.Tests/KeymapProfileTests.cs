using PhonePlayLink.Adb;
using PhonePlayLink.Keymap;

namespace PhonePlayLink.Tests;

public class KeymapProfileTests
{
    [Fact]
    public void SaveAndLoad_PreservesAllFourBindingTypesAndMetadata()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ppl_prof_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mgr = new ProfileManager(tempDir);
            var original = new KeymapProfile
            {
                Name = "Test Game FPS+MOBA",
                PackageName = "com.example.game",
                HintOpacity = 0.42f,
                ShowHints = false,
                Taps =
                [
                    new TapBinding { Label = "Fire", Key = InputKey.MouseLeft, X = 0.85f, Y = 0.72f, OnlyWhenAimLocked = true }
                ],
                Joysticks =
                [
                    new WasdJoystickBinding { CenterX = 0.18f, CenterY = 0.75f, Radius = 0.10f, SprintModifierKey = InputKey.LeftShift, SprintRadius = 0.15f }
                ],
                FpsAims =
                [
                    new FpsAimBinding { ToggleKey = InputKey.Grave, SensitivityX = 0.002f, SensitivityY = 0.0015f, InvertY = true }
                ],
                MobaSkills =
                [
                    new MobaSkillBinding { Label = "Ult", Key = InputKey.R, SkillX = 0.88f, SkillY = 0.60f, CastRadius = 0.12f, CancelX = 0.91f, CancelY = 0.18f }
                ]
            };

            string file = mgr.SaveToFile(original, "test_game");
            Assert.True(File.Exists(file));

            KeymapProfile loaded = mgr.LoadFromFile(file);
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.PackageName, loaded.PackageName);
            Assert.InRange(loaded.HintOpacity, 0.419f, 0.421f);
            Assert.False(loaded.ShowHints);

            Assert.Single(loaded.Taps);
            Assert.Equal(InputKey.MouseLeft, loaded.Taps[0].Key);
            Assert.True(loaded.Taps[0].OnlyWhenAimLocked);

            Assert.Single(loaded.Joysticks);
            Assert.Equal(InputKey.LeftShift, loaded.Joysticks[0].SprintModifierKey);
            Assert.InRange(loaded.Joysticks[0].SprintRadius, 0.149f, 0.151f);

            Assert.Single(loaded.FpsAims);
            Assert.True(loaded.FpsAims[0].InvertY);

            Assert.Single(loaded.MobaSkills);
            Assert.Equal(InputKey.R, loaded.MobaSkills[0].Key);
            Assert.InRange(loaded.MobaSkills[0].CancelX, 0.909f, 0.911f);

            var found = mgr.FindByPackageName("com.example.game");
            Assert.NotNull(found);
            Assert.Equal("Test Game FPS+MOBA", found!.Value.Profile.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DefaultJsonProfiles_LoadCleanlyFromRepositoryProfilesFolder()
    {
        string profilesDir = ProfileManager.ResolveDefaultDirectory();
        var mgr = new ProfileManager(profilesDir);

        KeymapProfile fps = mgr.LoadFromFile(Path.Combine(profilesDir, "fps_default.json"));
        Assert.NotEmpty(fps.Taps);
        Assert.Single(fps.Joysticks);
        Assert.Single(fps.FpsAims);

        KeymapProfile moba = mgr.LoadFromFile(Path.Combine(profilesDir, "moba_default.json"));
        Assert.NotEmpty(moba.Taps);
        Assert.Single(moba.Joysticks);
        Assert.Equal(3, moba.MobaSkills.Count);
    }

    [Fact]
    public void ParseForegroundPackage_ExtractsPackageFromDumpsysWindowOutput()
    {
        const string dumpsys = """
              mCurrentFocus=Window{9e1f2a u0 com.tencent.ig/com.epicgames.ue4.GameActivity}
              mFocusedApp=ActivityRecord{7c3b1 u0 com.tencent.ig/com.epicgames.ue4.GameActivity t45}
            """;

        string? pkg = AdbClient.ParseForegroundPackage(dumpsys);
        Assert.Equal("com.tencent.ig", pkg);
    }
}
