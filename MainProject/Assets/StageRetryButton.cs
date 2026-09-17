using UnityEngine;

/// <summary>
/// Makes a 3D object with a Collider (e.g. Settings/retry) restart the current stage when
/// clicked - the same as Retry on the stage result popup.
/// </summary>
[RequireComponent(typeof(Collider))]
public class StageRetryButton : MonoBehaviour
{
    // Fires only when press and release both land on this collider.
    private void OnMouseUpAsButton()
    {
        if (StageUIController.Instance == null)
        {
            Debug.LogWarning("[StageRetryButton] No StageUIController in the scene.", this);
            return;
        }
        StageUIController.Instance.RetryStage();
    }
}
