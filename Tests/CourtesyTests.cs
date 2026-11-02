using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        private static void CourtesyChecks()
        {
            Reset();
            var a=Body(0,0); var b=Body(0,1.6f);
            var walker=Walking(a,new(0,0,8)); walker.Tight=true;
            Traffic(walker,null,a.Position,new(0,0,1),0);
            Traffic(walker,null,a.Position,new(0,0,1),.7f);
            Check(_roomRequests.Count==1 && !_orders.ContainsKey(b.Id),"queue room requests without changing the enumerated orders");
            StartRoomRequests(.7f);
            var aside=_orders[b.Id];
            Check(aside.Courtesy && aside.CourtesyFor==a && aside.Suspended==null,"idle blocker accepts request to step aside");
            Check(Math.Abs(aside.Dest.x)>=1.3f && !NearDoor(aside.Dest),"aside destination clears the lane and door");
            Traffic(walker,null,a.Position,new(0,0,1),1);
            Check(walker.TrafficBlocked && WaitingForRoom(walker,1),"requesting walker waits while its partner moves");
            b.Position=aside.Dest+new Vector3(0,1,0);
            Check(WaitingForRoom(walker,2),"owner waits while partner settles inside arrival radius");
            aside.LocalStep=false;
            Check(!WaitingForRoom(walker,2) && !CourtesyFinished(aside,2),"partner holds aside while the walker still needs to pass");
            Traffic(walker,null,a.Position,new(0,0,1),2);
            Check(!walker.TrafficBlocked,"the walker resumes once the blocker has made room");
            a.Position=new(0,1,3);
            Check(CourtesyFinished(aside,3),"partner finishes after the owner has passed");
            _orders.Remove(b.Id); RestoreCourtesy(aside,3);
            Check(!_orders.ContainsKey(b.Id),"idle partner remains at the free spot");

            Reset();
            a=Body(0,0); b=Body(0,1.6f);
            walker=Walking(a,new(0,0,8)); walker.Tight=true;
            var original=Walking(b,new(0,0,-8)); original.Dir=new(0,0,-1);
            original.TrafficBlocked=true; original.Until=5;
            Check(CanMakeRoom(b,walker) && !CanMakeRoom(a,original),"only one of two stalled walkers yields by fixed priority");
            Traffic(walker,null,a.Position,new(0,0,1),0);
            Traffic(walker,null,a.Position,new(0,0,1),.7f);
            StartRoomRequests(.7f); aside=_orders[b.Id];
            Check(aside.Courtesy && aside.Suspended==original && !original.Requested,"stalled walk is suspended without replacing its goal");
            b.Position=aside.Dest+new Vector3(0,1,0); a.Position=new(0,1,3);
            _orders.Remove(b.Id); RestoreCourtesy(aside,3.7f);
            Check(_orders[b.Id]==original && original.Dest.z==-8 && original.Until==8,"resume original destination and extend its time by the pause");
            Check(!original.TrafficBlocked && original.PathAt==-100 && !original.ComInit,"resumed movement replans from the new location");
            aside.Suspended=original;
            Check(CancelCourtesy(aside)==original && aside.Suspended==null,"an explicit command cancels the suspended order");
            _orders.Remove(b.Id); RestoreCourtesy(aside,5);
            Check(!_orders.ContainsKey(b.Id),"a canceled suspension cannot resurrect its old goal");
            Check(CancelCourtesy(walker)==walker,"ordinary commands keep their original completion semantics");

            Reset();
            a=Body(0,0); b=Body(0,1.6f); walker=Walking(a,new(0,0,8));
            b.Reaching=true; Check(!CanMakeRoom(b,walker),"do not interrupt reaching"); b.Reaching=false;
            b.Handling=true; Check(!CanMakeRoom(b,walker),"do not interrupt hand recipes"); b.Handling=false;
            b.CoveringWound=true; Check(!CanMakeRoom(b,walker),"do not interrupt native wound pressure"); b.CoveringWound=false;
            b.Swing=true; Check(!CanMakeRoom(b,walker),"do not interrupt a strike"); b.Swing=false;
            FruitPose.Held.Add(b.Id); Check(!CanMakeRoom(b,walker),"do not interrupt held poses"); FruitPose.Held.Clear();
            b.Conscious=false; Check(!CanMakeRoom(b,walker),"unconscious bodies cannot cooperate"); b.Conscious=true;
            b.Upright=false; Check(!CanMakeRoom(b,walker),"fallen bodies cannot be asked to shuffle"); b.Upright=true;
            original=Walking(b,new(0,0,8));
            Check(!CanMakeRoom(b,walker),"ordinary moving goals retain control");
            original.Paused=true;
            Check(CanMakeRoom(b,walker),"resting follow orders can temporarily make room");
            original.Strikes=true; Check(!CanMakeRoom(b,walker),"combat orders retain control"); original.Strikes=false;
            original.Stance=true; Check(!CanMakeRoom(b,walker),"item stances retain control"); original.Stance=false;
            original.Raw=true; Check(!CanMakeRoom(b,walker),"raw locomotion retains control"); original.Raw=false;
            original.Turn=true; Check(!CanMakeRoom(b,walker),"explicit turning retains control"); original.Turn=false;
            original.Run=true; Check(!CanMakeRoom(b,walker),"running retains control");

            Reset();
            a=Body(0,0); b=Body(0,1.6f); walker=Walking(a,new(0,0,8)); walker.Tight=true;
            FruitNav.Clear=(from,to)=>false;
            Traffic(walker,null,a.Position,new(0,0,1),0);
            Traffic(walker,null,a.Position,new(0,0,1),.7f); StartRoomRequests(.7f);
            Check(!_orders.ContainsKey(b.Id) && walker.TrafficBlocked,"no room request drives through a wall");
            FruitNav.Clear=(from,to)=>true; FruitNav.SampleY=4;
            Traffic(walker,null,a.Position,new(0,0,1),1.6f); StartRoomRequests(1.6f);
            Check(!_orders.ContainsKey(b.Id),"no aside destination on a different floor");
            FruitNav.SampleY=0;
            Traffic(walker,null,a.Position,new(0,0,1),2.5f);
            var intervening=Walking(b,new(4,0,8)); StartRoomRequests(2.5f);
            Check(_orders[b.Id]==intervening,"a new command arriving before dispatch cancels the courtesy request");

            Reset();
            a=Body(0,0); b=Body(0,2.4f); walker=Walking(a,new(0,0,8));
            original=Walking(b,new(0,0,-8)); original.ComVel=new(0,0,-1.5f);
            var heading=Traffic(walker,null,a.Position,new(0,0,1),0);
            var held=Traffic(walker,null,a.Position,new Vector3(-.2f,0,1).normalized,.1f);
            Check(Vector3.Angle(heading,held)<.1f,"passing commitment survives changing pursuit direction");
            FruitNav.Clear=(from,to)=>false;
            held=Traffic(walker,null,a.Position,new(0,0,1),.2f);
            Check(walker.TrafficBlocked && Vector3.Angle(heading,held)<.1f,"an obstructed commitment waits without flipping sides");
            ResetCourtesy();
            Check(_roomRequests.Count==0 && _roomCooldown.Count==0,"scene reset clears negotiation state");

            Reset();
            a=Body(0,0); b=Body(0,1.6f); walker=Walking(a,new(0,0,8)); walker.Tight=true;
            // A narrow throat opens into a pocket farther along. Diagonal rays cut its wall.
            FruitNav.Allowed=p=>Math.Abs(p.x)<.4f || p.z>=3.5f;
            FruitNav.Clear=(from,to)=>
            {
                for(int i=0;i<=20;i++) if(!FruitNav.Allowed(from+(to-from)*(i/20f)))return false;
                return true;
            };
            walker.RoomForward=new(0,0,1);
            Traffic(walker,null,a.Position,new(0,0,1),0);
            Traffic(walker,null,a.Position,new Vector3(.3f,0,1).normalized,.7f);
            Check(_roomRequests.TryGetValue(b.Id,out var pocket) && pocket.Retreat,
                "corridor with no direct sidestep selects retreat then lateral pocket");
            Check(pocket.Waypoint.z>3.5f && Math.Abs(pocket.Waypoint.x)<.1f && Math.Abs(pocket.Spot.x)>1.3f,
                "retreat stays in throat until there is a full passing pocket");
            StartRoomRequests(.7f);aside=_orders[b.Id];
            Check(aside.LocalStep && aside.CourtesyRetreat && aside.LocalGoal.z==pocket.Waypoint.z,
                "courtesy commits first leg rather than cutting doorway corner");
            Check(WaitingForRoom(walker,2),"owner waits through the retreat leg");
            b.Position=aside.CourtesyWaypoint+new Vector3(0,1,0);
            Check(WaitingForRoom(walker,4),"reaching the junction does not prematurely release waiting walker");
            Check(LocalLegClear(b,b.Position,aside.Dest),"second lateral leg is clear from the junction");
            Reset();a=Body(0,0);b=Body(0,1.6f);walker=Walking(a,new(0,0,8));walker.Tight=true;
            FruitNav.Allowed=p=>Math.Abs(p.x)<.4f;
            FruitNav.Clear=(from,to)=>Math.Abs(from.x)<.4f&&Math.Abs(to.x)<.4f;
            Traffic(walker,null,a.Position,new(0,0,1),0);Traffic(walker,null,a.Position,new(0,0,1),.7f);
            Check(_roomRequests.Count==0,"no passing pocket means no invented escape through corridor walls");
        }
    }
}
