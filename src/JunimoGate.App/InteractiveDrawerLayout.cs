using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;

namespace JunimoGate.App;

[Register("org.junimogate.app.InteractiveDrawerLayout")]
public sealed class InteractiveDrawerLayout : DrawerLayout
{
    private bool contentSwipeEnabled;
    private bool remapGesture;
    private bool movementAccepted;
    private bool suppressNativeEdge;
    private float gestureStartX;
    private float gestureStartY;
    private float gestureOffsetX;
    private float edgeSuppressionOffsetX;
    private long gestureDownTime;
    private MetaKeyStates gestureMetaState;
    private readonly int touchSlop;
    private readonly Action cancelStationaryGesture;

    public InteractiveDrawerLayout(Context context) : base(context)
    {
        touchSlop = ReadTouchSlop(context);
        cancelStationaryGesture = CancelStationaryGesture;
    }

    public InteractiveDrawerLayout(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        touchSlop = ReadTouchSlop(context);
        cancelStationaryGesture = CancelStationaryGesture;
    }

    public InteractiveDrawerLayout(Context context, IAttributeSet? attrs, int defStyleAttr) :
        base(context, attrs, defStyleAttr)
    {
        touchSlop = ReadTouchSlop(context);
        cancelStationaryGesture = CancelStationaryGesture;
    }

    internal bool ContentSwipeEnabled
    {
        get => contentSwipeEnabled;
        set
        {
            contentSwipeEnabled = value;
            if (!value)
                ResetRemapping();
        }
    }

    public override bool OnInterceptTouchEvent(MotionEvent? ev)
    {
        if (ev is null)
            return base.OnInterceptTouchEvent(ev);

        PrepareRemapping(ev);
        using var remapped = CreateRemappedEvent(ev);
        var intercepted = base.OnInterceptTouchEvent(remapped ?? ev);
        ScheduleReset(ev);
        return intercepted;
    }

    public override bool OnTouchEvent(MotionEvent? ev)
    {
        if (ev is null)
            return base.OnTouchEvent(ev);

        PrepareRemapping(ev);
        using var remapped = CreateRemappedEvent(ev);
        var handled = base.OnTouchEvent(remapped ?? ev);
        ScheduleReset(ev);
        return handled;
    }

    private void PrepareRemapping(MotionEvent ev)
    {
        if (ev.ActionMasked == MotionEventActions.Down)
        {
            ResetRemapping();
            if (!contentSwipeEnabled || IsDrawerOpen(GravityCompat.Start))
                return;

            var density = Resources?.DisplayMetrics?.Density ?? 1f;
            var minimumX = 48f * density;
            var maximumX = 190f * density;
            gestureStartX = ev.GetX();
            gestureStartY = ev.GetY();
            gestureDownTime = ev.DownTime;
            gestureMetaState = ev.MetaState;
            suppressNativeEdge = gestureStartX < minimumX;
            edgeSuppressionOffsetX = suppressNativeEdge ? minimumX - gestureStartX : 0f;
            remapGesture = gestureStartX >= minimumX && gestureStartX <= maximumX;
            gestureOffsetX = remapGesture ? gestureStartX - density : 0f;
            if (remapGesture)
                PostDelayed(cancelStationaryGesture, 120L);
            return;
        }

        if (ev.ActionMasked == MotionEventActions.PointerDown)
        {
            CancelStationaryGesture();
            return;
        }
        if (ev.ActionMasked != MotionEventActions.Move || !remapGesture || movementAccepted)
            return;

        var deltaX = ev.GetX() - gestureStartX;
        var deltaY = Math.Abs(ev.GetY() - gestureStartY);
        if (deltaX > touchSlop && deltaX > deltaY * 1.2f)
        {
            movementAccepted = true;
            RemoveCallbacks(cancelStationaryGesture);
        }
        else if (deltaX < -touchSlop || deltaY > touchSlop)
        {
            CancelStationaryGesture();
        }
    }

    private MotionEvent? CreateRemappedEvent(MotionEvent ev)
    {
        var offset = remapGesture
            ? -gestureOffsetX
            : suppressNativeEdge
                ? edgeSuppressionOffsetX
                : 0f;
        if (offset == 0f)
            return null;
        var remapped = MotionEvent.Obtain(ev);
        remapped?.OffsetLocation(offset, 0f);
        return remapped;
    }

    private void CancelStationaryGesture()
    {
        if (!remapGesture || movementAccepted)
            return;
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        using var cancel = MotionEvent.Obtain(
            gestureDownTime,
            SystemClock.UptimeMillis(),
            MotionEventActions.Cancel,
            density,
            gestureStartY,
            gestureMetaState);
        if (cancel is not null)
        {
            _ = base.OnInterceptTouchEvent(cancel);
            _ = base.OnTouchEvent(cancel);
        }
        ResetRemapping();
    }

    private void ScheduleReset(MotionEvent ev)
    {
        if (ev.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            Post(ResetRemapping);
    }

    private void ResetRemapping()
    {
        RemoveCallbacks(cancelStationaryGesture);
        remapGesture = false;
        movementAccepted = false;
        suppressNativeEdge = false;
        gestureStartX = 0f;
        gestureStartY = 0f;
        gestureOffsetX = 0f;
        edgeSuppressionOffsetX = 0f;
        gestureDownTime = 0L;
        gestureMetaState = default;
    }

    private static int ReadTouchSlop(Context context) =>
        ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
}
