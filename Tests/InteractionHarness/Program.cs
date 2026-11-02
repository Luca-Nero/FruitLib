using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 zero=>default;
        public static Vector3 up=>new(0,1,0);
        public float magnitude=>MathF.Sqrt(x*x+y*y+z*z);
        public Vector3 normalized=>magnitude>0?this/magnitude:zero;
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float n)=>new(a.x*n,a.y*n,a.z*n);
        public static Vector3 operator /(Vector3 a,float n)=>a*(1/n);
        public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
    }
    public static class Time { public static float time; }
    public static class Mathf { public const float Deg2Rad=MathF.PI/180f; }
    public class Rigidbody {}
    public class Collider { public Rigidbody attachedRigidbody; }
    public struct RaycastHit { public Collider collider; public Vector3 point; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics
    {
        public static bool Wall;
        public static bool Raycast(Vector3 a,Vector3 d,out RaycastHit h,float n,int mask,QueryTriggerInteraction trigger)
        { h=new(){point=a+d*.1f,collider=new()};return Wall; }
    }
}
namespace FruitLib
{
    public class FruitLimb { public bool Present=true; public Vector3 LivePosition; }
    public class FruitBody
    {
        public int Id;
        public bool Alive=true,Conscious=true,Up=true;
        public Vector3 Position=new(0,.9f,0);
        public FruitLimb[] Limbs=new FruitLimb[15];
        public FruitBody(){for(int i=0;i<15;i++)Limbs[i]=new();}
    }
    public class FruitWorldItem
    {
        public bool Valid=true;public string Key="test-tool";public Rigidbody Rb=new();
        public float ToolReach;
        public Func<Vector3?> WorkingPoint;
        public Func<FruitBody,FruitWorldItem,FruitBody,Vector3?,FruitInteractionSpec> Interaction;
    }
    public static class FruitBodies
    {
        public static Dictionary<int,FruitBody> Bodies=new();
        public static FruitBody Get(int id)=>Bodies.GetValueOrDefault(id);
        public static List<string> Errors=new();
        public static void ReportOnce(string label,Exception e)=>Errors.Add(label+": "+e.Message);
    }
    public class FruitPoseShape
    {
        public static FruitPoseShape Lean=new(),Kneel=new(),Crouch=new(),Sit=new();
        public string Name="test-pose";
    }
    public static class FruitPose
    {
        public static Dictionary<int,FruitPoseShape> Poses=new();
        public static int Releases;
        public static bool CanHold=true;
        public static bool IsHolding(FruitBody b)=>Poses.ContainsKey(b.Id);
        public static bool IsReleasing(FruitBody b)=>false;
        public static FruitPoseShape Current(FruitBody b)=>Poses.GetValueOrDefault(b.Id);
        public static float Weight(FruitBody b)=>IsHolding(b)?1:0;
        public static bool Hold(FruitBody b,FruitPoseShape p,float t,string source)
        {if(!CanHold)return false;Poses[b.Id]=p;return true;}
        public static void Release(FruitBody b,float t){Poses.Remove(b.Id);Releases++;}
    }
    public static class FruitInteractionTrace
    {
        private static int n;
        public static string NewId(string name)=>name+":"+(++n);
        public static void Write(string id,string stage,FruitBody actor,Vector3 point,float reach,bool left=false,string detail="",Vector3? stance=null){}
    }
    public enum FruitPathStatus { Complete, Partial }
    public class FruitPath { public float Length; public FruitPathStatus Status; }
    public enum FruitHandsResult { Used }
    public static class FruitNav
    {
        public static bool Ready=true,Route=true;
        public static int Layers=1;
        public static Func<Vector3,float> Floor=p=>0;
        public static float Flat(Vector3 p)=>MathF.Sqrt(p.x*p.x+p.z*p.z);
        public static bool Under(Vector3 p,float radius,out Vector3 q)=>Sample(p,radius,out q);
        public static bool Sample(Vector3 p,float radius,out Vector3 q){q=p;q.y=Floor(p);return Ready;}
        public static bool FindPath(Vector3 from,Vector3 to,FruitPath p,float snap,bool measure,bool shape)
        {p.Length=Flat(to-from);p.Status=FruitPathStatus.Complete;return Route;}
    }
    public static partial class FruitBodyControl
    {
        private const int LeftHand=11,RightHand=14;
        private static readonly float[] StanceBearings={0,25,-25,50,-50,90,-90,180};
        private static FruitPath _probe=new();
        private class Hands { public FruitWorldItem Item; }
        private static Dictionary<int,Hands> _hands=new();
        public static bool SimulateContact=true,Occupied,Reaching,Moving,CanStep=true;
        public static int Steps,Walks;
        private static bool Upright(FruitBody b)=>b.Up;
        public static bool IsReaching(FruitBody b)=>Reaching;
        private static bool Swinging(FruitBody b)=>false;
        private static bool NativeCareBusy(FruitBody b)=>false;
        private static int HeldHand(FruitBody b)=>_hands.ContainsKey(b.Id)?0:-1;
        private static bool HasHand(FruitBody b,bool left)=>b.Limbs[left?11:14].Present;
        private static void Detach(Hands h,FruitHandsResult result,bool publish,bool notify){_hands.Clear();}
        public static FruitWorldItem Holding(FruitBody b)=>_hands.GetValueOrDefault(b.Id)?.Item;
        private static void Frame(FruitBody b,out Vector3 chest,out Vector3 fwd,out Vector3 right)
        {chest=b.Position+Vector3.up*.1f;fwd=new(0,0,1);right=new(1,0,0);}
        private static Vector3 LiveCentre(FruitBody b)=>b.Position;
        public static bool IsWalking(FruitBody b)=>Moving;
        private static bool IsTurning(FruitBody b)=>false;
        public static void Stop(FruitBody b){if(_interactionCommand==0)CancelInteraction(b);Moving=false;}
        private static void Face(FruitBody b,Vector3 direction,float seconds){}
        private static bool StepTo(FruitBody b,Vector3 point,float seconds)
        {if(!CanStep)return false;Steps++;b.Position=new(point.x,.9f,point.z);return true;}
        private static void GoTo(FruitBody b,Vector3 p,float stop,float seconds,bool run,bool legacy,bool oldFooting,Vector3? stance)
        {Walks++;Vector3 s=stance.Value;b.Position=new(s.x,.9f,s.z);}
        private static Vector3 FlatDirection(Vector3 p){p.y=0;return p.normalized;}
        private static bool Taken(Vector3 point,FruitBody actor,FruitBody target)=>Occupied;
        private static bool PlaceHand(FruitBody b,bool left,Vector3 point,float speed)
        {if(SimulateContact)b.Limbs[left?11:14].LivePosition=point;return true;}
        private static void ReleaseHand(FruitBody b,bool left){}

        private static int checks;
        private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
        private static FruitBody Body()
        {
            ResetInteractions();FruitBodies.Bodies.Clear();FruitPose.Poses.Clear();_hands.Clear();
            Time.time=0;SimulateContact=true;Occupied=false;Reaching=false;Moving=false;CanStep=true;
            FruitNav.Ready=true;FruitNav.Route=true;FruitNav.Floor=p=>0;Physics.Wall=false;FruitPose.CanHold=true;
            Steps=Walks=0;
            var b=new FruitBody{Id=1};FruitBodies.Bodies[1]=b;return b;
        }
        private static FruitInteractionSpec Machine(Action<FruitInteraction> apply=null)=>new()
        {
            Name="machine-button",ResolvePoint=()=>new Vector3(0,.9f,1),
            Begin=apply??(op=>op.Complete("machine changed state")),Postures=FruitInteractionPostures.Stand
        };
        private static void Pump(int count=35){for(int i=0;i<count;i++){Time.time+=.1f;TickInteractions();}}
        public static void Main()
        {
            var b=Body();int ended=0;FruitInteraction receipt=null;
            Check(Interact(b,Machine(),op=>{ended++;receipt=op;}),"machine without inventory is accepted");
            Check(InteractionOf(b).Stage==FruitInteractionStage.Accepted,"accepted is not completed");
            Pump();Check(ended==1&&receipt.Stage==FruitInteractionStage.Completed,"machine completes only after contact and apply");
            Check(Steps>0&&Walks==0,"nearby working point uses local steps");
            receipt.Complete();receipt.Fail("late");Check(ended==1,"terminal callbacks are idempotent");

            b=Body();FruitInteraction pending=null;ended=0;
            Interact(b,Machine(op=>pending=op),op=>ended++);Pump();
            Check(pending!=null&&pending.Stage==FruitInteractionStage.Applying&&ended==0,"deferred apply remains active");
            Check(!Interact(b,Machine()),"second attempt cannot overwrite active identity");
            pending.Complete("finished later");Check(ended==1,"deferred completion ends once");

            b=Body();pending=null;Interact(b,Machine(op=>pending=op));Pump();
            var old=pending;Stop(b);Check(old.Stage==FruitInteractionStage.Cancelled,"explicit stop cancels");
            Interact(b,Machine(op=>pending=op));var newer=InteractionOf(b);old.Complete();
            Check(InteractionOf(b)==newer&&!newer.Terminal,"stale callback cannot complete replacement");
            Pump();pending.Complete();

            b=Body();SimulateContact=false;receipt=null;var shortJob=Machine();shortJob.Seconds=2;
            Interact(b,shortJob,op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Failed,"nearby without hand contact never completes");
            Check(receipt.Detail.Contains("deadline"),"timeout explains failure");

            b=Body();pending=null;receipt=null;Interact(b,Machine(op=>pending=op),op=>receipt=op);Pump();
            b.Limbs[14].LivePosition=new(10,10,10);SimulateContact=false;Pump(1);
            Check(receipt.Stage==FruitInteractionStage.Failed&&receipt.Detail.Contains("contact"),"lost contact aborts delayed action");

            b=Body();receipt=null;Interact(b,Machine(),op=>receipt=op);b.Conscious=false;Pump(1);
            Check(receipt.Stage==FruitInteractionStage.Failed,"unconsciousness fails active attempt");
            b=Body();receipt=null;Interact(b,Machine(),op=>receipt=op);b.Up=false;Pump(1);
            Check(receipt.Stage==FruitInteractionStage.Failed,"lost footing fails active attempt");
            b=Body();receipt=null;Interact(b,Machine(),op=>receipt=op);FruitBodies.Bodies[1]=new FruitBody{Id=1};Pump(1);
            Check(receipt.Stage==FruitInteractionStage.Failed,"same ID with replacement body is not the same actor");

            foreach(string obstruction in new[]{"wall","occupied","route","floor"})
            {
                b=Body();receipt=null;Interact(b,Machine(),op=>receipt=op);
                if(obstruction=="wall")Physics.Wall=true;
                if(obstruction=="occupied")Occupied=true;
                if(obstruction=="route")FruitNav.Route=false;
                if(obstruction=="floor")FruitNav.Floor=p=>p.z>.3f?3:0;
                Pump();Check(receipt.Stage==FruitInteractionStage.Failed,"reject unreachable stance: "+obstruction);
            }

            b=Body();receipt=null;var broken=Machine();broken.ResolvePoint=()=>null;
            Interact(b,broken,op=>receipt=op);Pump(1);Check(receipt.Stage==FruitInteractionStage.Failed,"missing working point fails");
            b=Body();receipt=null;broken=Machine();broken.ResolvePoint=()=>new Vector3(float.NaN,0,0);
            Interact(b,broken,op=>receipt=op);Pump(1);Check(receipt.Stage==FruitInteractionStage.Failed,"NaN point fails");
            b=Body();broken=Machine();broken.Reach=float.NaN;Check(!Interact(b,broken),"reject invalid reach");
            broken=Machine();broken.Seconds=float.PositiveInfinity;Check(!Interact(b,broken),"reject unbounded deadline");
            broken=Machine();broken.Postures=0;Check(!Interact(b,broken),"reject no supported posture");
            b.Limbs[14].Present=false;Check(!Interact(b,Machine()),"reject missing working hand");

            b=Body();receipt=null;var tool=new FruitWorldItem();_hands[b.Id]=new(){Item=tool};
            var spec=Machine();spec.Item=tool;Interact(b,spec,op=>receipt=op);tool.Valid=false;Pump(1);
            Check(receipt.Stage==FruitInteractionStage.Failed,"invalid tool fails");
            b=Body();receipt=null;tool=new();_hands[b.Id]=new(){Item=tool};spec=Machine();spec.Item=tool;
            Interact(b,spec,op=>receipt=op);_hands.Clear();Pump(1);Check(receipt.Stage==FruitInteractionStage.Failed,"lost held tool fails");
            b=Body();tool=new();_hands[b.Id]=new(){Item=tool};spec=Machine(op=>
            {Check(ConsumeInteractionItem(op),"consumption detaches during apply");op.Complete();});spec.Item=tool;
            Interact(b,spec);var consumed=InteractionOf(b);Check(!ConsumeInteractionItem(consumed),"cannot consume before apply");Pump();
            Check(consumed.Stage==FruitInteractionStage.Completed&&Holding(b)==null,"consumption does not interrupt completion");

            b=Body();receipt=null;var poseSpec=Machine();poseSpec.Postures=FruitInteractionPostures.Kneel;
            Interact(b,poseSpec,op=>receipt=op);Pump();Check(receipt.Stage==FruitInteractionStage.Completed,"allowed kneeling work completes");
            Check(!FruitPose.IsHolding(b),"owned posture releases after completion");
            b=Body();FruitPose.Poses[b.Id]=FruitPoseShape.Sit;Check(!Interact(b,Machine()),"does not steal an existing pose");
            b=Body();receipt=null;poseSpec=Machine();poseSpec.Postures=FruitInteractionPostures.Kneel;FruitPose.CanHold=false;
            Interact(b,poseSpec,op=>receipt=op);Pump();Check(receipt.Stage==FruitInteractionStage.Failed,"failed posture is not assumed successful");

            b=Body();ended=0;int cancel=0;spec=Machine(op=>{});spec.Cancel=()=>cancel++;
            Interact(b,spec,op=>ended++);Pump();ResetInteractions();
            Check(ended==1&&cancel==1&&InteractionOf(b)==null,"scene reset cleans up and delivers one cancellation");
            Check(!FruitInteractionPolicy.Contact(.3f,.18f,true),"centre proximity does not authorize contact");
            Check(!FruitInteractionPolicy.Contact(.1f,.18f,false),"unsettled posture does not authorize contact");
            Check(FruitInteractionPolicy.ContactStart(false,.2f,.4f)==-1,"lost contact resets sustained timer");

            b=Body();bool machineAvailable=true;receipt=null;spec=Machine(op=>op.Apply(()=>{machineAvailable=false;return true;},"one-shot switch"));
            spec.Valid=()=>machineAvailable;Interact(b,spec,op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Completed&&!machineAvailable,"successful one-shot machine may invalidate its own prerequisites");
            Check(!receipt.Apply(()=>throw new Exception("must not execute")),"terminal operation cannot mutate world again");

            b=Body();pending=null;receipt=null;int applied=0;Interact(b,Machine(op=>pending=op),op=>receipt=op);Pump();
            b.Limbs[14].LivePosition=new(10,10,10);
            Check(!pending.Apply(()=>{applied++;return true;}),"delayed apply checks contact in this frame");
            Check(applied==0&&receipt.Stage==FruitInteractionStage.Failed,"lost contact prevents native mutation");

            b=Body();receipt=null;Interact(b,Machine(op=>op.Apply(()=>false,"switch refused")),op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Failed,"native refusal does not complete action");
            b=Body();receipt=null;Interact(b,Machine(op=>throw new InvalidOperationException("native failure")),op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Failed&&InteractionOf(b)==null,"throwing application cleans up and fails");

            b=Body();receipt=null;spec=Machine();spec.Target=b;spec.ResolvePoint=()=>b.Position+Vector3.up*.15f;
            Interact(b,spec,op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Completed&&Steps==0&&Walks==0,"self interaction does not chase its own moving body");

            b=Body();Vector3 machinePoint=new(0,.9f,1);spec=Machine();spec.ResolvePoint=()=>machinePoint;receipt=null;
            Interact(b,spec,op=>receipt=op);Pump(2);machinePoint=new(0,.9f,1.6f);Pump();
            Check(receipt.Stage==FruitInteractionStage.Completed&&receipt.Attempts==2,"moved working point gets a bounded reposition");
            b=Body();Check(TryInteractionStance(b,Machine(),out _,out _),"pickup can query shared geometry without taking lifecycle ownership");
            Check(InteractionOf(b)==null,"stance query never starts an action");

            b=Body();receipt=null;tool=new(){WorkingPoint=()=>new Vector3(20,20,20)};_hands[b.Id]=new(){Item=tool};
            spec=Machine();spec.Item=tool;spec.Seconds=2;Interact(b,spec,op=>receipt=op);Pump();
            Check(receipt.Stage==FruitInteractionStage.Failed,"declared tool reach cannot fake actual tip contact");
            Console.WriteLine("PASS: "+checks+" production interaction controller checks (native boundaries stubbed).");
        }
    }
}
