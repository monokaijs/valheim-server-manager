// Synthetic event boundary. VoiceSlider.OnMove is linked from production; these
// fixtures test value/focus dispatch, not Unity's native layout or input devices.
namespace UnityEngine.EventSystems
{
    internal enum MoveDirection { Left, Up, Right, Down, None }
    internal sealed class AxisEventData(MoveDirection direction)
    {
        internal MoveDirection moveDir=direction;
        internal bool Used;
        internal void Use()=>Used=true;
    }
}
namespace UnityEngine.UI
{
    internal class Slider
    {
        internal float value,minValue,maxValue;
        internal bool Active=true,Interactable=true;
        internal UnityEngine.EventSystems.MoveDirection NativeMove=UnityEngine.EventSystems.MoveDirection.None;
        protected bool IsActive()=>Active;
        protected bool IsInteractable()=>Interactable;
        public virtual void OnMove(UnityEngine.EventSystems.AxisEventData data)=>NativeMove=data.moveDir;
    }
}
