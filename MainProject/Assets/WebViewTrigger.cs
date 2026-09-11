using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Put this on a 3D object that has a Collider (e.g. the "guide" cube inside the Settings
/// prefab). Clicking that object is the only thing that brings the webview up: the page is
/// not fetched and the native webview is not created until the first click.
///
/// Requires WebViewController.loadOnStart = false and WebViewWindow.startHidden = true
/// (both are the defaults), otherwise the webview would already be up before any click.
/// </summary>
[RequireComponent(typeof(Collider))]
public class WebViewTrigger : MonoBehaviour
{
    [Header("References (found automatically when left empty)")]
    [Tooltip("Loads the page. Left empty, the first WebViewController in the scene is used.")]
    public WebViewController controller;

    [Tooltip("Window chrome to open. Left empty, the first WebViewWindow in the scene is used.")]
    public WebViewWindow window;

    [Header("Behaviour")]
    [Tooltip("Click again while the window is open to close it, instead of just re-opening it.")]
    public bool toggleOnClick = false;

    void Awake()
    {
        if (controller == null)
        {
#if UNITY_2023_1_OR_NEWER
            controller = FindFirstObjectByType<WebViewController>();
#else
            controller = FindObjectOfType<WebViewController>();
#endif
        }

        if (window == null)
        {
#if UNITY_2023_1_OR_NEWER
            window = FindFirstObjectByType<WebViewWindow>();
#else
            window = FindObjectOfType<WebViewWindow>();
#endif
        }
    }

    // OnMouseUpAsButton fires only when press and release both land on this collider,
    // which is the usual "clicked" semantic for a button.
    void OnMouseUpAsButton()
    {
        // OnMouse* messages ignore the EventSystem entirely, so without this the click
        // would pass straight through the open webview window to the cube behind it.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return;
        }

        Activate();
    }

    /// <summary>
    /// Loads the page on first use and shows the window. Safe to call repeatedly - the
    /// controller only performs the Init()/LoadURL() work once.
    /// </summary>
    public void Activate()
    {
        if (controller != null)
        {
            controller.Load();
        }

        if (window != null)
        {
            if (toggleOnClick)
            {
                window.Toggle();
            }
            else
            {
                window.Open();
            }
        }
        else if (controller != null)
        {
            // No window chrome in the scene - fall back to just showing the raw webview.
            controller.SetVisibility(true);
        }
    }
}
