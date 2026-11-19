namespace WinGlance.UI;

public enum AppCommand
{
    CancelOrExit,
    ToggleCheatsheet,
    ToggleCheatsheetExpanded,
    OpenFileDialog,
    OpenExternalEditor,
    NextImage,
    PreviousImage,
    FirstImage,
    LastImage,
    NextScene,
    PreviousScene,
    JumpToBurstHero,
    BurstKnockout,
    ToggleSlideshow,
    SlideshowIntervalIncrease,
    SlideshowIntervalDecrease,
    ToggleFullscreen,
    ToggleFocusPeaking,
    ToggleZoom100,
    AiEyeJump,
    ToggleClipping,
    ToggleFalseColor,
    CycleCursorLoupe,
    ToggleExifDossier,
    ToggleTheme,
    ToggleHistogram,
    ToggleFaceDock,
    ToggleFilmstrip,
    CycleCameraProfile,
    ToggleSplitCompare,
    SwapSplitPanes,
    TogglePick,
    ToggleReject,
    Unflag,
    SetRating0,
    SetRating1,
    SetRating2,
    SetRating3,
    SetRating4,
    SetRating5,
    ToggleColorRed,
    ToggleColorYellow,
    ToggleColorGreen,
    ToggleColorBlue,
    ToggleAutoAdvance,
    CycleFilter,
    TogglePicksOnlyFilter,
    RotateCW,
    RotateCCW,
    ExportCurrent,
    BatchExportPicks,
    TrashCurrent,
    PurgeRejects,
    Undo,
    Redo
}

public readonly record struct KeyInput(int VirtualKey, bool Ctrl, bool Shift, bool Alt, bool IsRepeat = false);

public static class CommandMap
{
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_PRIOR = 0x21;
    public const int VK_NEXT = 0x22;
    public const int VK_END = 0x23;
    public const int VK_HOME = 0x24;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_DELETE = 0x2E;
    public const int VK_ADD = 0x6B;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_F1 = 0x70;
    public const int VK_F11 = 0x7A;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_OEM_MINUS = 0xBD;
    public const int VK_OEM_2 = 0xBF; // '?' / '/'
    public const int VK_OEM_4 = 0xDB; // '['
    public const int VK_OEM_5 = 0xDC; // '\'
    public const int VK_OEM_6 = 0xDD; // ']'

    public static AppCommand? Resolve(KeyInput input, bool isCheatsheetOpen)
    {
        if (input.VirtualKey is VK_SHIFT or VK_CONTROL or VK_MENU)
        {
            return null;
        }

        if (isCheatsheetOpen)
        {
            if (input.VirtualKey == VK_ESCAPE) return AppCommand.CancelOrExit;
            if (input.VirtualKey == VK_SPACE) return input.IsRepeat ? null : AppCommand.ToggleCheatsheetExpanded;
            return input.IsRepeat ? null : AppCommand.ToggleCheatsheet;
        }

        if (input.Ctrl && !input.Alt)
        {
            return input.VirtualKey switch
            {
                'O' => AppCommand.OpenFileDialog,
                VK_RETURN => AppCommand.OpenExternalEditor,
                VK_RIGHT => input.IsRepeat ? null : AppCommand.NextScene,
                VK_LEFT => input.IsRepeat ? null : AppCommand.PreviousScene,
                'Z' => input.Shift ? AppCommand.Redo : AppCommand.Undo,
                'Y' => AppCommand.Redo,
                'E' => AppCommand.BatchExportPicks,
                'F' => AppCommand.CycleFilter,
                _ => null
            };
        }

        if (input.Alt)
        {
            return null;
        }

        // Prevent accidental key-repeat toggling on stateful / destructive commands
        bool repeat = input.IsRepeat;

        switch (input.VirtualKey)
        {
            case VK_ESCAPE:
            case 'Q':
                return repeat ? null : AppCommand.CancelOrExit;

            case VK_F1:
            case VK_OEM_2:
                return repeat ? null : AppCommand.ToggleCheatsheet;

            case VK_RIGHT:
            case VK_DOWN:
            case VK_NEXT:
            case VK_SPACE:
            case 'N':
            case 'J':
                return AppCommand.NextImage;

            case VK_LEFT:
            case VK_UP:
            case VK_PRIOR:
            case 'K':
                return AppCommand.PreviousImage;

            case VK_HOME:
                return AppCommand.FirstImage;

            case VK_END:
                return AppCommand.LastImage;

            case VK_OEM_6: // ']'
                return repeat ? null : AppCommand.NextScene;

            case VK_OEM_4: // '['
                return repeat ? null : AppCommand.PreviousScene;

            case VK_RETURN:
                return repeat ? null : AppCommand.ToggleSlideshow;

            case VK_OEM_PLUS:
            case VK_ADD:
                return AppCommand.SlideshowIntervalIncrease;

            case VK_OEM_MINUS:
            case VK_SUBTRACT:
                return AppCommand.SlideshowIntervalDecrease;

            case 'F':
                return repeat ? null : (input.Shift ? AppCommand.ToggleFocusPeaking : AppCommand.ToggleFullscreen);

            case VK_F11:
                return repeat ? null : AppCommand.ToggleFullscreen;

            case 'Z':
                return repeat ? null : (input.Shift ? AppCommand.AiEyeJump : AppCommand.ToggleZoom100);

            case 'O':
                return repeat ? null : (input.Shift ? AppCommand.ToggleFalseColor : AppCommand.ToggleClipping);

            case 'M':
                return repeat ? null : AppCommand.CycleCursorLoupe;

            case 'I':
                return repeat ? null : AppCommand.ToggleExifDossier;

            case 'T':
            case 'D':
                return repeat ? null : AppCommand.ToggleTheme;

            case 'H':
                return repeat ? null : AppCommand.ToggleHistogram;

            case 'A':
                return repeat ? null : AppCommand.ToggleFaceDock;

            case 'G':
                return repeat ? null : AppCommand.ToggleFilmstrip;

            case 'V':
                return repeat ? null : AppCommand.CycleCameraProfile;

            case 'S':
                return repeat ? null : AppCommand.ToggleSplitCompare;

            case VK_TAB:
                return repeat ? null : AppCommand.SwapSplitPanes;

            case 'P':
                return repeat ? null : (input.Shift ? AppCommand.BurstKnockout : AppCommand.TogglePick);

            case 'B':
                return repeat ? null : AppCommand.JumpToBurstHero;

            case 'X':
                return repeat ? null : AppCommand.ToggleReject;

            case 'U':
                return repeat ? null : AppCommand.Unflag;

            case '0': return repeat ? null : AppCommand.SetRating0;
            case '1': return repeat ? null : AppCommand.SetRating1;
            case '2': return repeat ? null : AppCommand.SetRating2;
            case '3': return repeat ? null : AppCommand.SetRating3;
            case '4': return repeat ? null : AppCommand.SetRating4;
            case '5': return repeat ? null : AppCommand.SetRating5;

            case '6': return repeat ? null : AppCommand.ToggleColorRed;
            case '7': return repeat ? null : AppCommand.ToggleColorYellow;
            case '8': return repeat ? null : AppCommand.ToggleColorGreen;
            case '9': return repeat ? null : AppCommand.ToggleColorBlue;

            case 'C':
                return repeat ? null : AppCommand.ToggleAutoAdvance;

            case VK_OEM_5:
                return repeat ? null : (input.Shift ? AppCommand.TogglePicksOnlyFilter : AppCommand.CycleFilter);

            case 'R':
                return AppCommand.RotateCW;

            case 'L':
                return AppCommand.RotateCCW;

            case 'E':
                return repeat ? null : (input.Shift ? AppCommand.BatchExportPicks : AppCommand.ExportCurrent);

            case VK_DELETE:
            case VK_BACK:
                return repeat ? null : (input.Shift ? AppCommand.PurgeRejects : AppCommand.TrashCurrent);

            default:
                return null;
        }
    }
}
