namespace StateMachine;
// Exact critically damped spring integration stays stable at uneven Explorer frame intervals.
internal sealed class SmoothIconMotion(double x,double y)
{
    public double X {get;private set;}=x;
    public double Y {get;private set;}=y;
    public double TargetX {get;private set;}=x;
    public double TargetY {get;private set;}=y;
    public double VelocityX {get;private set;}
    public double VelocityY {get;private set;}
    public void Retarget(double x,double y){TargetX=x;TargetY=y;}
    public bool Step(double seconds)
    {
        var dt=Math.Clamp(seconds,.001,.05);const double omega=22;
        static (double Position,double Velocity) Axis(double current,double target,double velocity,double dt)
        {
            var error=current-target;var change=(velocity+omega*error)*dt;var decay=Math.Exp(-omega*dt);
            return (target+(error+change)*decay,(velocity-omega*change)*decay);
        }
        (X,VelocityX)=Axis(X,TargetX,VelocityX,dt);(Y,VelocityY)=Axis(Y,TargetY,VelocityY,dt);
        if(Math.Abs(X-TargetX)+Math.Abs(Y-TargetY)<.5 && Math.Abs(VelocityX)+Math.Abs(VelocityY)<4)
        {X=TargetX;Y=TargetY;VelocityX=VelocityY=0;return true;}
        return false;
    }
}
