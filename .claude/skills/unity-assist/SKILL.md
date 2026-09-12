# Role & Goal
You are an expert C# / Unity developer. Your task is to implement full Windows Editor and Windows Standalone support in `WebViewObject.cs` for a Unity WebView plugin (using Microsoft WebView2).

Currently, many methods and platform conditional directives in `WebViewObject.cs` handle Windows (`UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN`) with empty stubs or placeholder returns like:
```csharp
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        //TODO: UNSUPPORTED
        return true;

```

You need to refactor and fill in all these Windows placeholders by following the exact implementation pattern used for macOS (`UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX`).

---

# Key Requirements

1. **Platform Conditional Directive Update:**
* Expand preprocessor directives handling macOS (`UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX`) to also include Windows Editor and Standalone (`UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN`).
* Example target platforms: `#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN`


2. **DLL Imports (`[DllImport("WebView")]`):**
* Ensure `[DllImport("WebView")]` functions are available under Windows conditions so native C++ plugin calls (`_CWebViewPlugin_Init`, `_CWebViewPlugin_Update`, `_CWebViewPlugin_Render`, `_CWebViewPlugin_GetMessage`, etc.) function identically to Mac.


3. **Field & Property Exposure:**
* Expose `public GameObject canvas;`, `public int bitmapRefreshCycle = 3;`, and `public int devicePixelRatio = 1;` so they are accessible and editable in the Unity Inspector when developing on Windows.


4. **Lifecycle & Rendering Logic Implementation:**
* **`Init()`**: Initialize `webView` instance pointer using `_CWebViewPlugin_Init` on Windows.
* **`SetMargins()`**: Calculate pixel dimensions and invoke `_CWebViewPlugin_SetRect`.
* **`LoadURL()` / `SetVisibility()**`: Forward calls directly to `_CWebViewPlugin_LoadURL` and `_CWebViewPlugin_SetVisibility`.
* **`Update()`**:
* Poll native messages via `_CWebViewPlugin_GetMessage()` and route callbacks (`CallFromJS`, `CallOnError`, `CallOnLoaded`, `CallOnStarted`).
* Perform offscreen rendering bitmap updates: check `bitmapRefreshCycle`, call `_CWebViewPlugin_Update`, allocate/update `Texture2D`, pin memory, and call `_CWebViewPlugin_Render`.


* **`OnGUI()`**:
* Handle mouse/keyboard events (`_CWebViewPlugin_SendMouseEvent`, `_CWebViewPlugin_SendKeyEvent`).
* Render the offscreen texture overlay on screen via `Graphics.DrawTexture(rect, texture)`.




5. **Clean Code & Fallbacks:**
* Remove all `//TODO: UNSUPPORTED` stubs for Windows.
* Retain existing code for Android, iOS, and WebGL without breaking their functionalities.



---

# Action Item

Please inspect `WebViewObject.cs` and replace all unsupported Windows template blocks with functional Offscreen Rendering C# code matching the macOS implementation structure.

## Output & Response Style

- **Strict Token Limit:** Do not write narrative explanations, post-mortems, or conversational filler.
- **Format:** Provide only raw, actionable bullet points or a numbered list of execution steps.
- **Example Transformation:**
  - *Instead of:* "I found two bugs. Bug 1 was your anchor logic failing because..."
  - *Output:* 
    1. Tag Main Camera as 'MainCamera' (or drag to Anchor Camera).
    2. Enter Play mode to test runtime margins.

```