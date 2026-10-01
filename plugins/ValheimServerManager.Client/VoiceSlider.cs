using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimServerManager.Client;

// Unity's continuous Slider moves by 10% per keyboard/controller press. Voice
// controls need fine, predictable steps while retaining native pointer dragging.
internal sealed class VoiceSlider : Slider
{
    internal float Step = .01f;
    public override void OnMove(AxisEventData eventData)
    {
        if (!IsActive() || !IsInteractable()) return;
        if (eventData.moveDir == MoveDirection.Left || eventData.moveDir == MoveDirection.Right)
        {
            value = Mathf.Clamp(value + (eventData.moveDir == MoveDirection.Left ? -Step : Step), minValue, maxValue);
            eventData.Use();
        }
        else base.OnMove(eventData);
    }
}
