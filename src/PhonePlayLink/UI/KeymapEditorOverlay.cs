using System.Numerics;
using ImGuiNET;
using PhonePlayLink.Input;
using PhonePlayLink.Keymap;

namespace PhonePlayLink.UI;

/// <summary>
/// Lớp phủ Dear ImGui vẽ:
/// 1) Visual Keymap Editor (F1): lớp phủ mờ nhẹ, thanh công cụ nổi ở mép trên, kéo thả nút trực tiếp trên màn hình game, bảng Thuộc tính nhanh.
/// 2) Play HUD Hints (F3): gợi ý phím mờ (click-through) khi đang chơi.
/// </summary>
public sealed class KeymapEditorOverlay
{
    private enum NodeKind { None, Tap, Wasd, FpsAim, MobaSkill, MobaCancel, MobaAnchor }
    private enum KeyAssignTarget { None, TapKey, WasdUp, WasdLeft, WasdDown, WasdRight, WasdSprint, AimToggle, AimSecondary, MobaSkillKey }

    private readonly KeymapEngine _engine;
    private readonly ProfileManager _profileManager;
    private readonly PackageWatcher? _packageWatcher;

    private string _currentProfilePath;
    private List<string> _availableProfiles = [];
    private int _selectedProfileIdx = -1;
    private string _profileNameBuf = string.Empty;
    private string _statusMessage = string.Empty;
    private double _statusTimer;

    private NodeKind _selectedKind = NodeKind.None;
    private int _selectedIndex = -1;
    private NodeKind _draggingKind = NodeKind.None;
    private int _draggingIndex = -1;
    private bool _wasMouseLeftDown;

    private KeyAssignTarget _listeningTarget = KeyAssignTarget.None;
    private bool _isEditorOpen;

    public bool IsEditorOpen => _isEditorOpen;
    public bool IsListeningForKey => _listeningTarget != KeyAssignTarget.None;

    public KeymapEditorOverlay(
        KeymapEngine engine,
        ProfileManager profileManager,
        string initialProfilePath,
        PackageWatcher? packageWatcher = null)
    {
        _engine = engine;
        _profileManager = profileManager;
        _currentProfilePath = initialProfilePath;
        _packageWatcher = packageWatcher;
        _profileNameBuf = _engine.Profile.Name;
        RefreshProfileList();
    }

    public void ToggleEditor()
    {
        _isEditorOpen = !_isEditorOpen;
        _listeningTarget = KeyAssignTarget.None;
        _draggingKind = NodeKind.None;

        if (_isEditorOpen)
        {
            _engine.ReleaseAllInputs();
            _profileNameBuf = _engine.Profile.Name;
            RefreshProfileList();
        }
        else
        {
            _engine.RebuildRuntimes();
        }
    }

    public void SetEditorOpen(bool open)
    {
        if (_isEditorOpen != open)
            ToggleEditor();
    }

    public void ToggleHints()
    {
        _engine.Profile.ShowHints = !_engine.Profile.ShowHints;
        SetStatus(_engine.Profile.ShowHints ? "Đã bật gợi ý phím" : "Đã ẩn gợi ý phím");
    }

    public bool TryCaptureKeyForEditor(InputKey key)
    {
        if (!_isEditorOpen || _listeningTarget == KeyAssignTarget.None)
            return false;

        if (key == InputKey.Escape)
        {
            _listeningTarget = KeyAssignTarget.None;
            return true;
        }

        if (key == InputKey.None)
            return true;

        KeymapProfile p = _engine.Profile;
        switch (_listeningTarget)
        {
            case KeyAssignTarget.TapKey when _selectedKind == NodeKind.Tap && _selectedIndex < p.Taps.Count:
                p.Taps[_selectedIndex].Key = key;
                break;
            case KeyAssignTarget.WasdUp when _selectedKind == NodeKind.Wasd && _selectedIndex < p.Joysticks.Count:
                p.Joysticks[_selectedIndex].UpKey = key;
                break;
            case KeyAssignTarget.WasdLeft when _selectedKind == NodeKind.Wasd && _selectedIndex < p.Joysticks.Count:
                p.Joysticks[_selectedIndex].LeftKey = key;
                break;
            case KeyAssignTarget.WasdDown when _selectedKind == NodeKind.Wasd && _selectedIndex < p.Joysticks.Count:
                p.Joysticks[_selectedIndex].DownKey = key;
                break;
            case KeyAssignTarget.WasdRight when _selectedKind == NodeKind.Wasd && _selectedIndex < p.Joysticks.Count:
                p.Joysticks[_selectedIndex].RightKey = key;
                break;
            case KeyAssignTarget.WasdSprint when _selectedKind == NodeKind.Wasd && _selectedIndex < p.Joysticks.Count:
                p.Joysticks[_selectedIndex].SprintModifierKey = key;
                break;
            case KeyAssignTarget.AimToggle when _selectedKind == NodeKind.FpsAim && _selectedIndex < p.FpsAims.Count:
                p.FpsAims[_selectedIndex].ToggleKey = key;
                break;
            case KeyAssignTarget.AimSecondary when _selectedKind == NodeKind.FpsAim && _selectedIndex < p.FpsAims.Count:
                p.FpsAims[_selectedIndex].SecondaryToggleKey = key;
                break;
            case KeyAssignTarget.MobaSkillKey when (_selectedKind is NodeKind.MobaSkill or NodeKind.MobaCancel or NodeKind.MobaAnchor) && _selectedIndex < p.MobaSkills.Count:
                p.MobaSkills[_selectedIndex].Key = key;
                break;
        }

        _listeningTarget = KeyAssignTarget.None;
        _engine.RebuildRuntimes();
        return true;
    }

    public void OnProfileSwitchedExternally(string filePath, KeymapProfile newProfile)
    {
        _currentProfilePath = filePath;
        _profileNameBuf = newProfile.Name;
        _selectedKind = NodeKind.None;
        _selectedIndex = -1;
        RefreshProfileList();
        SetStatus($"Tự động nạp profile: {newProfile.Name}");
    }

    public bool ShouldBuildUiFrame() => _isEditorOpen || _engine.Profile.ShowHints || _engine.IsAimLocked || _statusTimer > 0.0;

    public void BuildUi(
        int windowWidth,
        int windowHeight,
        int videoWidth,
        int videoHeight,
        bool preserveAspectRatio,
        float deltaSeconds,
        float reservedRightPx = 0f)
    {
        if (_statusTimer > 0.0)
            _statusTimer = Math.Max(0.0, _statusTimer - deltaSeconds);

        VideoViewport vp = ViewportMapper.ComputeViewport(
            windowWidth,
            windowHeight,
            videoWidth,
            videoHeight,
            preserveAspectRatio,
            reservedRightPx);

        if (_isEditorOpen)
        {
            // Phủ lớp mờ nhẹ lên vùng game để nổi bật các nút Keymap khi chỉnh sửa
            float availW = Math.Max(1f, windowWidth - Math.Max(0f, reservedRightPx));
            ImGui.GetBackgroundDrawList().AddRectFilled(
                Vector2.Zero,
                new Vector2(availW, windowHeight),
                ColorU32(0.03f, 0.04f, 0.07f, 0.42f));

            HandleCanvasDragAndSelect(vp);
            DrawKeymapNodes(vp, isEditor: true);
            DrawTopEditorToolbar(availW);
            DrawNodeInspector(availW);
        }
        else
        {
            if (_engine.Profile.ShowHints && _engine.Profile.HintOpacity > 0.01f)
            {
                DrawKeymapNodes(vp, isEditor: false);
            }
            DrawPlayStatusBadge(windowWidth, reservedRightPx);
        }
    }

    private void HandleCanvasDragAndSelect(VideoViewport vp)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        bool mouseDown = io.MouseDown[0];
        bool mouseClicked = mouseDown && !_wasMouseLeftDown;
        bool mouseReleased = !mouseDown && _wasMouseLeftDown;
        _wasMouseLeftDown = mouseDown;

        if (mouseReleased)
        {
            if (_draggingKind != NodeKind.None)
            {
                _draggingKind = NodeKind.None;
                _draggingIndex = -1;
                _engine.RebuildRuntimes();
            }
            return;
        }

        Vector2 mp = io.MousePos;
        if (mouseClicked && !io.WantCaptureMouse)
        {
            if (TryHitTestNode(mp, vp, out NodeKind kind, out int idx))
            {
                _selectedKind = kind;
                _selectedIndex = idx;
                _draggingKind = kind;
                _draggingIndex = idx;
                _listeningTarget = KeyAssignTarget.None;
            }
            else
            {
                _selectedKind = NodeKind.None;
                _selectedIndex = -1;
                _listeningTarget = KeyAssignTarget.None;
            }
        }

        if (mouseDown && _draggingKind != NodeKind.None && vp.Width > 1f && vp.Height > 1f)
        {
            float nx = Math.Clamp((mp.X - vp.X) / vp.Width, 0.01f, 0.99f);
            float ny = Math.Clamp((mp.Y - vp.Y) / vp.Height, 0.01f, 0.99f);
            KeymapProfile p = _engine.Profile;

            switch (_draggingKind)
            {
                case NodeKind.Tap when _draggingIndex < p.Taps.Count:
                    p.Taps[_draggingIndex].X = nx;
                    p.Taps[_draggingIndex].Y = ny;
                    break;
                case NodeKind.Wasd when _draggingIndex < p.Joysticks.Count:
                    p.Joysticks[_draggingIndex].CenterX = nx;
                    p.Joysticks[_draggingIndex].CenterY = ny;
                    break;
                case NodeKind.FpsAim when _draggingIndex < p.FpsAims.Count:
                    p.FpsAims[_draggingIndex].CenterX = nx;
                    p.FpsAims[_draggingIndex].CenterY = ny;
                    break;
                case NodeKind.MobaSkill when _draggingIndex < p.MobaSkills.Count:
                    p.MobaSkills[_draggingIndex].SkillX = nx;
                    p.MobaSkills[_draggingIndex].SkillY = ny;
                    break;
                case NodeKind.MobaCancel when _draggingIndex < p.MobaSkills.Count:
                    p.MobaSkills[_draggingIndex].CancelX = nx;
                    p.MobaSkills[_draggingIndex].CancelY = ny;
                    break;
                case NodeKind.MobaAnchor when _draggingIndex < p.MobaSkills.Count:
                    p.MobaSkills[_draggingIndex].AnchorX = nx;
                    p.MobaSkills[_draggingIndex].AnchorY = ny;
                    break;
            }
        }
    }

    private bool TryHitTestNode(Vector2 mousePos, VideoViewport vp, out NodeKind kind, out int index)
    {
        KeymapProfile p = _engine.Profile;
        float minDim = MathF.Min(vp.Width, vp.Height);

        for (int i = p.Taps.Count - 1; i >= 0; i--)
        {
            Vector2 c = NormToScreen(p.Taps[i].X, p.Taps[i].Y, vp);
            float r = MathF.Max(18f, p.Taps[i].VisualRadius * minDim);
            if (Vector2.DistanceSquared(mousePos, c) <= r * r)
            {
                kind = NodeKind.Tap;
                index = i;
                return true;
            }
        }

        for (int i = p.MobaSkills.Count - 1; i >= 0; i--)
        {
            var s = p.MobaSkills[i];
            if (Vector2.Distance(mousePos, NormToScreen(s.CancelX, s.CancelY, vp)) <= 18f)
            {
                kind = NodeKind.MobaCancel;
                index = i;
                return true;
            }
            if (Vector2.Distance(mousePos, NormToScreen(s.AnchorX, s.AnchorY, vp)) <= 16f)
            {
                kind = NodeKind.MobaAnchor;
                index = i;
                return true;
            }
            if (Vector2.Distance(mousePos, NormToScreen(s.SkillX, s.SkillY, vp)) <= 26f)
            {
                kind = NodeKind.MobaSkill;
                index = i;
                return true;
            }
        }

        for (int i = p.FpsAims.Count - 1; i >= 0; i--)
        {
            Vector2 c = NormToScreen(p.FpsAims[i].CenterX, p.FpsAims[i].CenterY, vp);
            if (Vector2.Distance(mousePos, c) <= 28f)
            {
                kind = NodeKind.FpsAim;
                index = i;
                return true;
            }
        }

        for (int i = p.Joysticks.Count - 1; i >= 0; i--)
        {
            Vector2 c = NormToScreen(p.Joysticks[i].CenterX, p.Joysticks[i].CenterY, vp);
            float r = MathF.Max(28f, p.Joysticks[i].Radius * minDim * 0.6f);
            if (Vector2.Distance(mousePos, c) <= r)
            {
                kind = NodeKind.Wasd;
                index = i;
                return true;
            }
        }

        kind = NodeKind.None;
        index = -1;
        return false;
    }

    private void DrawKeymapNodes(VideoViewport vp, bool isEditor)
    {
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        KeymapProfile p = _engine.Profile;
        float alpha = isEditor ? MathF.Max(0.88f, p.HintOpacity) : Math.Clamp(p.HintOpacity, 0f, 1f);
        float minDim = MathF.Min(vp.Width, vp.Height);

        // 1. WASD Joysticks
        for (int i = 0; i < p.Joysticks.Count; i++)
        {
            var joy = p.Joysticks[i];
            Vector2 c = NormToScreen(joy.CenterX, joy.CenterY, vp);
            float r = MathF.Max(26f, joy.Radius * minDim);
            bool sel = isEditor && _selectedKind == NodeKind.Wasd && _selectedIndex == i;

            uint fill = ColorU32(0.00f, 0.71f, 0.85f, 0.16f * alpha);
            uint border = sel ? ColorU32(1f, 0.85f, 0.2f, 0.98f) : ColorU32(0.00f, 0.78f, 0.92f, 0.88f * alpha);
            dl.AddCircleFilled(c, r, fill, 36);
            dl.AddCircle(c, r, border, 36, sel ? 3f : 2f);

            if (joy.SprintModifierKey != InputKey.None && joy.SprintRadius > joy.Radius)
            {
                float sr = joy.SprintRadius * minDim;
                dl.AddCircle(c, sr, ColorU32(1f, 0.62f, 0.22f, 0.58f * alpha), 40, 1.5f);
                if (isEditor)
                {
                    DrawCenteredText(dl, new Vector2(c.X, c.Y - sr - 10f), $"Chạy nhanh: {joy.SprintModifierKey.ToShortLabel()}", ColorU32(1f, 0.75f, 0.35f, alpha));
                }
            }

            float kOff = r * 0.65f;
            uint txtCol = ColorU32(1f, 1f, 1f, alpha);
            DrawCenteredText(dl, new Vector2(c.X, c.Y - kOff), joy.UpKey.ToShortLabel(), txtCol);
            DrawCenteredText(dl, new Vector2(c.X - kOff, c.Y), joy.LeftKey.ToShortLabel(), txtCol);
            DrawCenteredText(dl, new Vector2(c.X, c.Y + kOff), joy.DownKey.ToShortLabel(), txtCol);
            DrawCenteredText(dl, new Vector2(c.X + kOff, c.Y), joy.RightKey.ToShortLabel(), txtCol);
        }

        // 2. FPS Aims
        for (int i = 0; i < p.FpsAims.Count; i++)
        {
            var aim = p.FpsAims[i];
            Vector2 c = NormToScreen(aim.CenterX, aim.CenterY, vp);
            bool sel = isEditor && _selectedKind == NodeKind.FpsAim && _selectedIndex == i;

            if (isEditor)
            {
                Vector2 topLeft = NormToScreen(aim.CenterX - aim.ZoneHalfWidth, aim.CenterY - aim.ZoneHalfHeight, vp);
                Vector2 botRight = NormToScreen(aim.CenterX + aim.ZoneHalfWidth, aim.CenterY + aim.ZoneHalfHeight, vp);
                dl.AddRect(topLeft, botRight, ColorU32(1f, 0.85f, 0.2f, 0.45f * alpha), 6f, ImDrawFlags.None, 1.5f);
            }

            uint col = sel ? ColorU32(1f, 0.9f, 0.2f, 1f) : ColorU32(1f, 0.75f, 0.15f, 0.90f * alpha);
            dl.AddCircleFilled(c, 20f, ColorU32(0.10f, 0.12f, 0.17f, 0.72f * alpha), 24);
            dl.AddCircle(c, 20f, col, 24, sel ? 3f : 2f);
            dl.AddLine(new Vector2(c.X - 10f, c.Y), new Vector2(c.X + 10f, c.Y), col, 1.5f);
            dl.AddLine(new Vector2(c.X, c.Y - 10f), new Vector2(c.X, c.Y + 10f), col, 1.5f);
            DrawCenteredText(dl, new Vector2(c.X, c.Y + 28f), $"Xoay tâm [{aim.ToggleKey.ToShortLabel()}]", ColorU32(1f, 0.92f, 0.4f, alpha));
        }

        // 3. MOBA Skills
        for (int i = 0; i < p.MobaSkills.Count; i++)
        {
            var s = p.MobaSkills[i];
            Vector2 skillPos = NormToScreen(s.SkillX, s.SkillY, vp);
            float castR = MathF.Max(22f, s.CastRadius * minDim);
            bool sel = isEditor && _selectedIndex == i && (_selectedKind is NodeKind.MobaSkill or NodeKind.MobaCancel or NodeKind.MobaAnchor);

            uint ringCol = sel ? ColorU32(1f, 0.85f, 0.2f, 1f) : ColorU32(0.75f, 0.40f, 1f, 0.88f * alpha);
            dl.AddCircleFilled(skillPos, 20f, ColorU32(0.30f, 0.14f, 0.52f, 0.72f * alpha), 28);
            dl.AddCircle(skillPos, 20f, ringCol, 28, sel ? 3f : 2f);
            if (isEditor)
            {
                dl.AddCircle(skillPos, castR, ColorU32(0.75f, 0.40f, 1f, 0.45f * alpha), 32, 1.5f);
                Vector2 cancelPos = NormToScreen(s.CancelX, s.CancelY, vp);
                Vector2 anchorPos = NormToScreen(s.AnchorX, s.AnchorY, vp);
                dl.AddLine(skillPos, cancelPos, ColorU32(0.95f, 0.30f, 0.30f, 0.45f * alpha), 1.2f);
                dl.AddCircleFilled(cancelPos, 13f, ColorU32(0.78f, 0.16f, 0.16f, 0.85f * alpha), 20);
                DrawCenteredText(dl, cancelPos, "X", ColorU32(1f, 1f, 1f, alpha));
                DrawCenteredText(dl, new Vector2(cancelPos.X, cancelPos.Y + 18f), "Hủy", ColorU32(1f, 0.65f, 0.65f, alpha));

                dl.AddCircleFilled(anchorPos, 10f, ColorU32(0.10f, 0.82f, 0.62f, 0.82f * alpha), 16);
                DrawCenteredText(dl, new Vector2(anchorPos.X, anchorPos.Y + 16f), $"Tâm ({s.Key.ToShortLabel()})", ColorU32(0.6f, 1f, 0.8f, alpha));
            }
            DrawCenteredText(dl, skillPos, s.Key.ToShortLabel(), ColorU32(1f, 1f, 1f, alpha));
        }

        // 4. Tap Keys
        for (int i = 0; i < p.Taps.Count; i++)
        {
            var tap = p.Taps[i];
            Vector2 c = NormToScreen(tap.X, tap.Y, vp);
            float r = MathF.Max(16f, tap.VisualRadius * minDim);
            bool sel = isEditor && _selectedKind == NodeKind.Tap && _selectedIndex == i;

            uint fill = tap.OnlyWhenAimLocked
                ? ColorU32(0.85f, 0.28f, 0.20f, 0.65f * alpha)
                : ColorU32(0.10f, 0.12f, 0.17f, 0.78f * alpha);
            uint border = sel
                ? ColorU32(1f, 0.85f, 0.2f, 1f)
                : ColorU32(0.00f, 0.75f, 0.90f, 0.88f * alpha);

            dl.AddCircleFilled(c, r, fill, 24);
            dl.AddCircle(c, r, border, 24, sel ? 3f : 1.8f);
            DrawCenteredText(dl, c, tap.Key.ToShortLabel(), ColorU32(1f, 1f, 1f, alpha));
            if (isEditor && !string.IsNullOrWhiteSpace(tap.Label))
            {
                DrawCenteredText(dl, new Vector2(c.X, c.Y + r + 10f), tap.Label, ColorU32(0.92f, 0.95f, 1f, alpha));
            }
        }
    }

    private void DrawTopEditorToolbar(float availWidth)
    {
        float barW = MathF.Min(920f, MathF.Max(520f, availWidth - 24f));
        ImGui.SetNextWindowPos(new Vector2(availWidth * 0.5f, 10f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowSize(new Vector2(barW, 0f), ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##TopKeymapToolbar", flags))
        {
            ImGui.End();
            return;
        }

        KeymapProfile p = _engine.Profile;

        // Hàng 1: Profile JSON + Lưu + Tạo mới + Gắn game + Đóng
        ImGui.SetNextItemWidth(175f);
        string preview = (_selectedProfileIdx >= 0 && _selectedProfileIdx < _availableProfiles.Count)
            ? Path.GetFileName(_availableProfiles[_selectedProfileIdx])
            : "Chọn Profile...";
        if (ImGui.BeginCombo("##ProfileCombo", preview))
        {
            for (int i = 0; i < _availableProfiles.Count; i++)
            {
                bool isSel = i == _selectedProfileIdx;
                string fname = Path.GetFileName(_availableProfiles[i]);
                if (ImGui.Selectable(fname, isSel))
                {
                    _selectedProfileIdx = i;
                    KeymapProfile loaded = _profileManager.LoadFromFile(_availableProfiles[i]);
                    _currentProfilePath = _availableProfiles[i];
                    _profileNameBuf = loaded.Name;
                    _selectedKind = NodeKind.None;
                    _selectedIndex = -1;
                    _engine.LoadProfile(loaded);
                    SetStatus($"Đã tải {fname}");
                }
                if (isSel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(155f);
        ImGui.InputText("##ProfileName", ref _profileNameBuf, 64);

        ImGui.SameLine();
        if (ImGui.Button("Lưu"))
        {
            p.Name = string.IsNullOrWhiteSpace(_profileNameBuf) ? "Custom" : _profileNameBuf.Trim();
            _currentProfilePath = _profileManager.SaveToFile(p, p.Name);
            RefreshProfileList();
            SetStatus($"Đã lưu {Path.GetFileName(_currentProfilePath)}");
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.22f, 0.30f, 1f));
        if (ImGui.Button("Tạo mới"))
        {
            var blank = KeymapProfile.CreateBlank("New Game");
            _profileNameBuf = blank.Name;
            _selectedKind = NodeKind.None;
            _selectedIndex = -1;
            _engine.LoadProfile(blank);
            SetStatus("Đã tạo profile trống");
        }
        ImGui.PopStyleColor();

        string? curPkg = _packageWatcher?.CurrentPackage;
        if (!string.IsNullOrWhiteSpace(curPkg))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.45f, 0.36f, 1f));
            if (ImGui.Button("Gắn game đang mở"))
            {
                p.PackageName = curPkg;
                p.Name = string.IsNullOrWhiteSpace(_profileNameBuf) ? curPkg : _profileNameBuf.Trim();
                _currentProfilePath = _profileManager.SaveToFile(p, curPkg);
                _packageWatcher!.NotifyManualProfileBound(curPkg);
                RefreshProfileList();
                SetStatus($"Đã gắn cho {curPkg}");
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Gắn profile hiện tại vào gói: {curPkg}");
        }

        ImGui.SameLine();
        float closeBtnW = 132f;
        float availX = ImGui.GetContentRegionAvail().X;
        if (availX > closeBtnW)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - closeBtnW);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.08f, 0.62f, 0.42f, 1f));
        if (ImGui.Button("Đóng & Chơi (F1)", new Vector2(closeBtnW, 0)))
        {
            SetEditorOpen(false);
        }
        ImGui.PopStyleColor();

        ImGui.Separator();

        // Hàng 2: 4 nút thêm nhanh + Thanh trượt Độ mờ phím
        if (ImGui.Button("+ Nút bấm (Tap)"))
        {
            p.Taps.Add(new TapBinding { Label = "Nút mới", Key = InputKey.F, X = 0.5f, Y = 0.5f });
            _selectedKind = NodeKind.Tap;
            _selectedIndex = p.Taps.Count - 1;
            _engine.RebuildRuntimes();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Di chuyển (WASD)"))
        {
            p.Joysticks.Add(new WasdJoystickBinding());
            _selectedKind = NodeKind.Wasd;
            _selectedIndex = p.Joysticks.Count - 1;
            _engine.RebuildRuntimes();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Xoay góc nhìn (FPS Aim)"))
        {
            p.FpsAims.Add(new FpsAimBinding());
            _selectedKind = NodeKind.FpsAim;
            _selectedIndex = p.FpsAims.Count - 1;
            _engine.RebuildRuntimes();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Tung chiêu (MOBA)"))
        {
            p.MobaSkills.Add(new MobaSkillBinding());
            _selectedKind = NodeKind.MobaSkill;
            _selectedIndex = p.MobaSkills.Count - 1;
            _engine.RebuildRuntimes();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        float opacityPct = p.HintOpacity * 100f;
        if (ImGui.SliderFloat("Độ mờ phím", ref opacityPct, 0f, 100f, "%.0f%%"))
        {
            p.HintOpacity = Math.Clamp(opacityPct / 100f, 0f, 1f);
        }

        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextColored(new Vector4(0.30f, 0.95f, 0.60f, 1f), _statusMessage);
        }
        else
        {
            ImGui.TextDisabled("Mẹo: Kéo thả trực tiếp các vòng tròn trên màn hình game | Nhấn vào nút để chỉnh phím & thông số.");
        }

        ImGui.End();
    }

    private void DrawNodeInspector(float availWidth)
    {
        if (_selectedKind == NodeKind.None || _selectedIndex < 0)
            return;

        ImGui.SetNextWindowPos(new Vector2(Math.Max(14f, availWidth - 320f), 108f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(305f, 0f), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Thuộc tính nhanh", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }

        KeymapProfile p = _engine.Profile;
        switch (_selectedKind)
        {
            case NodeKind.Tap when _selectedIndex < p.Taps.Count:
            {
                TapBinding tap = p.Taps[_selectedIndex];
                string lbl = tap.Label;
                if (ImGui.InputText("Nhãn", ref lbl, 32)) tap.Label = lbl;
                DrawKeyAssignButton("Phím gán", tap.Key, KeyAssignTarget.TapKey);
                bool aimOnly = tap.OnlyWhenAimLocked;
                if (ImGui.Checkbox("Chỉ bấm khi khóa tâm", ref aimOnly)) tap.OnlyWhenAimLocked = aimOnly;
                float r = tap.VisualRadius;
                if (ImGui.SliderFloat("Kích thước", ref r, 0.015f, 0.08f, "%.3f")) tap.VisualRadius = r;
                DrawDeleteButton(() => p.Taps.RemoveAt(_selectedIndex));
                break;
            }
            case NodeKind.Wasd when _selectedIndex < p.Joysticks.Count:
            {
                WasdJoystickBinding joy = p.Joysticks[_selectedIndex];
                DrawKeyAssignButton("Lên (W)", joy.UpKey, KeyAssignTarget.WasdUp);
                DrawKeyAssignButton("Trái (A)", joy.LeftKey, KeyAssignTarget.WasdLeft);
                DrawKeyAssignButton("Xuống (S)", joy.DownKey, KeyAssignTarget.WasdDown);
                DrawKeyAssignButton("Phải (D)", joy.RightKey, KeyAssignTarget.WasdRight);
                DrawKeyAssignButton("Chạy nhanh", joy.SprintModifierKey, KeyAssignTarget.WasdSprint);
                float r = joy.Radius;
                if (ImGui.SliderFloat("Bán kính", ref r, 0.04f, 0.25f, "%.3f")) joy.Radius = r;
                float sr = joy.SprintRadius;
                if (ImGui.SliderFloat("Bán kính chạy", ref sr, 0.04f, 0.30f, "%.3f")) joy.SprintRadius = sr;
                float ms = joy.TransitionMs;
                if (ImGui.SliderFloat("Độ mượt", ref ms, 0f, 20f, "%.1f ms")) joy.TransitionMs = ms;
                DrawDeleteButton(() => p.Joysticks.RemoveAt(_selectedIndex));
                break;
            }
            case NodeKind.FpsAim when _selectedIndex < p.FpsAims.Count:
            {
                FpsAimBinding aim = p.FpsAims[_selectedIndex];
                DrawKeyAssignButton("Phím khóa", aim.ToggleKey, KeyAssignTarget.AimToggle);
                DrawKeyAssignButton("Phím phụ", aim.SecondaryToggleKey, KeyAssignTarget.AimSecondary);
                float sx = aim.SensitivityX * 1000f;
                if (ImGui.SliderFloat("Độ nhạy X", ref sx, 0.2f, 5.0f, "%.2f")) aim.SensitivityX = sx / 1000f;
                float sy = aim.SensitivityY * 1000f;
                if (ImGui.SliderFloat("Độ nhạy Y", ref sy, 0.2f, 5.0f, "%.2f")) aim.SensitivityY = sy / 1000f;
                float zw = aim.ZoneHalfWidth;
                if (ImGui.SliderFloat("Vùng xoay X", ref zw, 0.08f, 0.45f, "%.2f")) aim.ZoneHalfWidth = zw;
                float zh = aim.ZoneHalfHeight;
                if (ImGui.SliderFloat("Vùng xoay Y", ref zh, 0.08f, 0.45f, "%.2f")) aim.ZoneHalfHeight = zh;
                bool inv = aim.InvertY;
                if (ImGui.Checkbox("Đảo trục Y", ref inv)) aim.InvertY = inv;
                DrawDeleteButton(() => p.FpsAims.RemoveAt(_selectedIndex));
                break;
            }
            case NodeKind.MobaSkill or NodeKind.MobaCancel or NodeKind.MobaAnchor when _selectedIndex < p.MobaSkills.Count:
            {
                MobaSkillBinding skill = p.MobaSkills[_selectedIndex];
                string lbl = skill.Label;
                if (ImGui.InputText("Tên chiêu", ref lbl, 32)) skill.Label = lbl;
                DrawKeyAssignButton("Phím chiêu", skill.Key, KeyAssignTarget.MobaSkillKey);
                float cr = skill.CastRadius;
                if (ImGui.SliderFloat("Bán kính kéo", ref cr, 0.04f, 0.25f, "%.3f")) skill.CastRadius = cr;
                bool rmbCancel = skill.CancelWithRightClick;
                if (ImGui.Checkbox("Chuột phải hủy chiêu", ref rmbCancel)) skill.CancelWithRightClick = rmbCancel;
                DrawDeleteButton(() => p.MobaSkills.RemoveAt(_selectedIndex));
                break;
            }
        }

        ImGui.End();
    }

    private void DrawDeleteButton(Action onDelete)
    {
        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.75f, 0.18f, 0.20f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.24f, 0.26f, 1.00f));
        if (ImGui.Button("Xóa nút", new Vector2(-1, 0)))
        {
            onDelete();
            ClearSelectionAndRebuild();
        }
        ImGui.PopStyleColor(2);
    }

    private void DrawKeyAssignButton(string label, InputKey current, KeyAssignTarget target)
    {
        bool waiting = _listeningTarget == target;
        string btnText = waiting ? ">> Nhấn phím bất kỳ... <<" : $"{current}##{target}";
        ImGui.Text(label);
        ImGui.SameLine(115f);
        if (ImGui.Button(btnText, new Vector2(-1, 0)))
        {
            _listeningTarget = waiting ? KeyAssignTarget.None : target;
        }
    }

    private void DrawPlayStatusBadge(int windowWidth, float reservedRightPx)
    {
        if (!_engine.IsAimLocked && _statusTimer <= 0.0)
            return;

        float availW = Math.Max(200f, windowWidth - reservedRightPx);
        ImGui.SetNextWindowPos(new Vector2(availW * 0.5f, 12f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.68f);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs;

        if (ImGui.Begin("##PlayBadge", flags))
        {
            if (_engine.IsAimLocked)
            {
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.25f, 1f), "ĐANG KHÓA TÂM FPS (~ / RightCtrl để mở chuột | F1 Editor)");
            }
            if (_statusTimer > 0.0 && !string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextColored(new Vector4(0.25f, 0.95f, 0.65f, 1f), _statusMessage);
            }
            ImGui.End();
        }
    }

    private void ClearSelectionAndRebuild()
    {
        _selectedKind = NodeKind.None;
        _selectedIndex = -1;
        _listeningTarget = KeyAssignTarget.None;
        _engine.RebuildRuntimes();
    }

    private void RefreshProfileList()
    {
        _availableProfiles = _profileManager.ListProfileFiles().ToList();
        _selectedProfileIdx = _availableProfiles.FindIndex(f =>
            string.Equals(Path.GetFullPath(f), Path.GetFullPath(_currentProfilePath), StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimer = 3.0;
    }

    private static Vector2 NormToScreen(float nx, float ny, VideoViewport vp)
        => new(vp.X + nx * vp.Width, vp.Y + ny * vp.Height);

    private static void DrawCenteredText(ImDrawListPtr dl, Vector2 center, string text, uint color)
    {
        Vector2 sz = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(center.X - sz.X * 0.5f, center.Y - sz.Y * 0.5f), color, text);
    }

    private static uint ColorU32(float r, float g, float b, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, Math.Clamp(a, 0f, 1f)));
}
