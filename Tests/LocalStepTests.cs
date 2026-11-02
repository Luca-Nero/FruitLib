using System;
using UnityEngine;
namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        private static void LocalStepChecks()
        {
            Reset(); var b=Body(0,0); var o=Walking(b,new(0,0,5)); o.Until=20;
            Check(BeginLocalStep(o,new(.8f,0,0),0),"lateral leg accepted on level clear floor");
            Check(TickLocalStep(o,true,.1f)==0 && o.Runtime.TargetMoveDirection.x>.99f && Math.Abs(o.Runtime.TargetMoveDirection.z)<.01f,
                "native lateral command does not become a forward arc");
            Check(o.Runtime.PuppeteerTargetYRot==0,"sidestep holds original yaw");
            b.Position=new(.7f,1,0);
            Check(TickLocalStep(o,true,1)==0 && o.Runtime.TargetMoveDirection.sqrMagnitude==0 && TickLocalStep(o,true,1.5f)==1 && !o.LocalStep && !o.Requested,"short step arrives and releases gait");
            b.Position=new(0,1,0);
            Check(BeginLocalStep(o,new(0,0,-.5f),2),"backstep accepted");
            TickLocalStep(o,true,2.1f);
            Check(o.Runtime.TargetMoveDirection.z<-.7f && o.Runtime.PuppeteerTargetYRot==0,"backstep keeps facing forward");
            Check(TickLocalStep(o,true,4.1f)==-1 && !o.Requested,"motionless body stops after independent progress window");
            Check(BeginLocalStep(o,new(.8f,0,0),5),"new step after a failed attempt");
            o.PathAt=100; o.ProgressAt=100; o.ProgressLeft=float.MaxValue;
            Check(TickLocalStep(o,true,7.1f)==-1,"path replanning cannot renew a jammed step");
            FruitNav.Allowed=p=>p.x<.3f || p.x>.5f;
            Check(!BeginLocalStep(o,new(.8f,0,0),8),"floor hole between clear endpoints rejects step");
            FruitNav.Allowed=p=>true; FruitNav.FloorHeight=p=>p.x>.3f?-.4f:0;
            Check(!BeginLocalStep(o,new(.8f,0,0),8),"drop beyond step-height rejects local leg");
            FruitNav.FloorHeight=p=>p.y>2f?3f:0f;
            Check(!BeginLocalStep(o,new(.8f,4f,0),8),"explicit destination on another storey rejects step");
            FruitNav.FloorHeight=p=>0; Geometry=(p,d,l)=>p.y<1.2f;
            Check(!BeginLocalStep(o,new(.8f,0,0),8),"chest obstacle above walkable floor rejects step");
            Geometry=(p,d,l)=>true; FruitNav.Clear=(a,z)=>false;
            Check(!BeginLocalStep(o,new(.8f,0,0),8),"wall between endpoints rejects step");
            FruitNav.Clear=(a,z)=>true;
            Check(!BeginLocalStep(o,new(3.1f,0,0),8),"local step cannot become an unbounded route");
            var contact=Body(0,.5f);
            Check(BeginLocalStep(o,new(0,0,-.5f),8) && TickLocalStep(o,true,8.1f)==0 && o.Requested,
                "close contact permits genuine separating backstep");
            contact.Position=new(.8f,1,0);
            Check(BeginLocalStep(o,new(0,0,-.5f),9),"safe retreat selected before crossing traffic");
            contact.Position=new(0,1,-.4f);
            Check(TickLocalStep(o,true,9.1f)==-1 && !o.Requested,"new stationary blocker invalidates committed leg");
            contact.Position=new(3,1,3);
            Check(BeginLocalStep(o,new(.8f,0,0),10),"yaw-drift trial starts");
            o.Runtime.CreatureYRotation=40;
            Check(TickLocalStep(o,true,10.1f)==-1,"excess body rotation aborts sideways gait");
        }
    }
}

