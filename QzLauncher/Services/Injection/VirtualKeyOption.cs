namespace QzLauncher.Services.Injection;

public class VirtualKeyOption
{
    public int KeyCode { get; set; }
    public string KeyName { get; set; } = string.Empty;

    public override string ToString() => KeyName;

    private static List<VirtualKeyOption>? _cachedList;

    public static List<VirtualKeyOption> GetCommonKeys()
    {
        if (_cachedList != null) return _cachedList;

        var list = new List<VirtualKeyOption>
        {
            // 功能键
            new() { KeyCode = 112, KeyName = "F1" },
            new() { KeyCode = 113, KeyName = "F2" },
            new() { KeyCode = 114, KeyName = "F3" },
            new() { KeyCode = 115, KeyName = "F4" },
            new() { KeyCode = 116, KeyName = "F5" },
            new() { KeyCode = 117, KeyName = "F6" },
            new() { KeyCode = 118, KeyName = "F7" },
            new() { KeyCode = 119, KeyName = "F8" },
            new() { KeyCode = 120, KeyName = "F9" },
            new() { KeyCode = 121, KeyName = "F10" },
            new() { KeyCode = 122, KeyName = "F11" },
            new() { KeyCode = 123, KeyName = "F12" },

            // 导航编辑键
            new() { KeyCode = 45, KeyName = "Insert" },
            new() { KeyCode = 46, KeyName = "Delete" },
            new() { KeyCode = 36, KeyName = "Home" },
            new() { KeyCode = 35, KeyName = "End" },
            new() { KeyCode = 33, KeyName = "PageUp" },
            new() { KeyCode = 34, KeyName = "PageDown" },

            // 常用功能与控制键
            new() { KeyCode = 192, KeyName = "~ (波浪线)" },
            new() { KeyCode = 9,   KeyName = "Tab" },
            new() { KeyCode = 20,  KeyName = "CapsLock" },
            new() { KeyCode = 32,  KeyName = "Space" },
            new() { KeyCode = 13,  KeyName = "Enter" },
            new() { KeyCode = 8,   KeyName = "Backspace" },

            // 数字行键
            new() { KeyCode = 48, KeyName = "0" },
            new() { KeyCode = 49, KeyName = "1" },
            new() { KeyCode = 50, KeyName = "2" },
            new() { KeyCode = 51, KeyName = "3" },
            new() { KeyCode = 52, KeyName = "4" },
            new() { KeyCode = 53, KeyName = "5" },
            new() { KeyCode = 54, KeyName = "6" },
            new() { KeyCode = 55, KeyName = "7" },
            new() { KeyCode = 56, KeyName = "8" },
            new() { KeyCode = 57, KeyName = "9" },

            // 小键盘
            new() { KeyCode = 96,  KeyName = "NumPad 0" },
            new() { KeyCode = 97,  KeyName = "NumPad 1" },
            new() { KeyCode = 98,  KeyName = "NumPad 2" },
            new() { KeyCode = 99,  KeyName = "NumPad 3" },
            new() { KeyCode = 100, KeyName = "NumPad 4" },
            new() { KeyCode = 101, KeyName = "NumPad 5" },
            new() { KeyCode = 102, KeyName = "NumPad 6" },
            new() { KeyCode = 103, KeyName = "NumPad 7" },
            new() { KeyCode = 104, KeyName = "NumPad 8" },
            new() { KeyCode = 105, KeyName = "NumPad 9" },
            new() { KeyCode = 106, KeyName = "NumPad *" },
            new() { KeyCode = 107, KeyName = "NumPad +" },
            new() { KeyCode = 109, KeyName = "NumPad -" },
            new() { KeyCode = 111, KeyName = "NumPad /" },

            // 常用字母
            new() { KeyCode = 65, KeyName = "A" },
            new() { KeyCode = 66, KeyName = "B" },
            new() { KeyCode = 67, KeyName = "C" },
            new() { KeyCode = 68, KeyName = "D" },
            new() { KeyCode = 69, KeyName = "E" },
            new() { KeyCode = 70, KeyName = "F" },
            new() { KeyCode = 71, KeyName = "G" },
            new() { KeyCode = 72, KeyName = "H" },
            new() { KeyCode = 73, KeyName = "I" },
            new() { KeyCode = 74, KeyName = "J" },
            new() { KeyCode = 75, KeyName = "K" },
            new() { KeyCode = 76, KeyName = "L" },
            new() { KeyCode = 77, KeyName = "M" },
            new() { KeyCode = 78, KeyName = "N" },
            new() { KeyCode = 79, KeyName = "O" },
            new() { KeyCode = 80, KeyName = "P" },
            new() { KeyCode = 81, KeyName = "Q" },
            new() { KeyCode = 82, KeyName = "R" },
            new() { KeyCode = 83, KeyName = "S" },
            new() { KeyCode = 84, KeyName = "T" },
            new() { KeyCode = 85, KeyName = "U" },
            new() { KeyCode = 86, KeyName = "V" },
            new() { KeyCode = 87, KeyName = "W" },
            new() { KeyCode = 88, KeyName = "X" },
            new() { KeyCode = 89, KeyName = "Y" },
            new() { KeyCode = 90, KeyName = "Z" },
        };

        _cachedList = list;
        return _cachedList;
    }
}
