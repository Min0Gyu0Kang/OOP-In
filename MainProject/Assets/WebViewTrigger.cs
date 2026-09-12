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
            controller = Resolve<WebViewController>();
        }

        if (window == null)
        {
            window = Resolve<WebViewWindow>();
        }
    }

    /// <summary>
    /// Finds the component this trigger should drive, narrowest scope first: this
    /// GameObject, then its parents, and only then the whole scene.
    ///
    /// The scene-wide fallback is a safety net, not a wiring strategy - with more than one
    /// webview in the scene (a reference page and an IDE, say) it cannot know which one the
    /// trigger means, so it warns and names what it picked. Assign the reference in the
    /// Inspector to make the choice explicit.
    /// </summary>
    T Resolve<T>() where T : Component
    {
        var local = GetComponentInParent<T>();
        if (local != null)
        {
            return local;
        }

#if UNITY_2023_1_OR_NEWER
        var found = FindObjectsByType<T>(FindObjectsSortMode.InstanceID);
#else
        var found = FindObjectsOfType<T>();
#endif
        if (found == null || found.Length == 0)
        {
            return null;
        }

        if (found.Length > 1)
        {
            Debug.LogWarning("[WebViewTrigger] '" + name + "' has no " + typeof(T).Name +
                             " assigned and found " + found.Length + " in the scene - using '" +
                             found[0].name + "'. Assign it in the Inspector to pick deliberately.",
                             this);
        }

        return found[0];
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
