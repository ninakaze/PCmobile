namespace PhonePlayLink.Keymap;

public sealed class TapBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "Tap";
    public InputKey Key { get; set; } = InputKey.Space;
    public float X { get; set; } = 0.5f;
    public float Y { get; set; } = 0.5f;
    public float VisualRadius { get; set; } = 0.032f;
    public bool OnlyWhenAimLocked { get; set; }
}

public sealed class WasdJoystickBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "WASD";
    public float CenterX { get; set; } = 0.18f;
    public float CenterY { get; set; } = 0.72f;
    public float Radius { get; set; } = 0.10f;
    public InputKey UpKey { get; set; } = InputKey.W;
    public InputKey LeftKey { get; set; } = InputKey.A;
    public InputKey DownKey { get; set; } = InputKey.S;
    public InputKey RightKey { get; set; } = InputKey.D;
    public InputKey SprintModifierKey { get; set; } = InputKey.LeftShift;
    public float SprintRadius { get; set; } = 0.145f;
    public float TransitionMs { get; set; } = 8.0f;
}

public sealed class FpsAimBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "FPS Aim";
    public InputKey ToggleKey { get; set; } = InputKey.Grave;
    public InputKey SecondaryToggleKey { get; set; } = InputKey.RightCtrl;
    public float CenterX { get; set; } = 0.72f;
    public float CenterY { get; set; } = 0.48f;
    public float ZoneHalfWidth { get; set; } = 0.22f;
    public float ZoneHalfHeight { get; set; } = 0.36f;
    public float SensitivityX { get; set; } = 0.0012f;
    public float SensitivityY { get; set; } = 0.0012f;
    public bool InvertY { get; set; }
}

public sealed class MobaSkillBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "Skill";
    public InputKey Key { get; set; } = InputKey.Q;
    public float SkillX { get; set; } = 0.78f;
    public float SkillY { get; set; } = 0.72f;
    public float CastRadius { get; set; } = 0.10f;
    public float AnchorX { get; set; } = 0.50f;
    public float AnchorY { get; set; } = 0.50f;
    public float CancelX { get; set; } = 0.90f;
    public float CancelY { get; set; } = 0.18f;
    public bool CancelWithRightClick { get; set; } = true;
}

public sealed class KeymapProfile
{
    public string Name { get; set; } = "Default";
    public string? PackageName { get; set; }
    public float HintOpacity { get; set; } = 0.65f;
    public bool ShowHints { get; set; } = true;

    public List<TapBinding> Taps { get; set; } = [];
    public List<WasdJoystickBinding> Joysticks { get; set; } = [];
    public List<FpsAimBinding> FpsAims { get; set; } = [];
    public List<MobaSkillBinding> MobaSkills { get; set; } = [];

    public static KeymapProfile CreateBlank(string name = "Custom") => new()
    {
        Name = name,
        HintOpacity = 0.70f,
        ShowHints = true
    };
}
