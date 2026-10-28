namespace FontsTry;

// Zdarzenia klawiatury do logowania, np. --events KeyDown albo --events KeyDown,KeyUp
[Flags]
public enum KeyboardEvents
{
    None = 0,
    KeyDown = 1,
    KeyUp = 2,
    All = KeyDown | KeyUp
}
