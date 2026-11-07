"""Exercise the generated server-operation Lua against small isolated fakes."""
import json
import sys
from pathlib import Path
from lupa import lua51, luajit21

scripts = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
setup = r'''
local function vec(x,y,z) return {x=x,y=y,z=z,Length=function(self) return 5 end} end
p={valid=true,frozen=false,id=7,events={}}
function p:UserID() return self.id end
function p:EntIndex() return 10 end
function p:GetPos() return vec(1,2,3) end
function p:GetVelocity() return vec(3,4,0) end
function p:Alive() return true end
function p:Health() return 100 end
function p:GetMaxHealth() return 100 end
function p:IsFrozen() return self.frozen end
function p:Freeze(v) self.frozen=v end
function p:IsOnGround() return true end
function p:GetMoveType() return 2 end
function p:InVehicle() return false end
function p:ChatPrint(s) self.message=s end
function p:IsPlayer() return true end
function p:IsBot() return false end
function IsValid(x) return x~=nil and x.valid==true end
function istable(x) return type(x)=='table' end
function isstring(x) return type(x)=='string' end
function isfunction(x) return type(x)=='function' end
player={GetHumans=function() return {p} end}
game={GetMap=function() return 'test_map' end}
function CurTime() return 12 end
function SysTime() return 123 end
ents={GetAll=function() return {1,2,3} end}
local hooks={Think={one=function() end,two=true},Tick={three=true}}
hook={GetTable=function() return hooks end}
function hook.Add(event,id,fn) hooks[event]=hooks[event] or {};hooks[event][id]=fn end
function hook.Remove(event,id) if hooks[event] then hooks[event][id]=nil end end
util={TableToJSON=function(t) snapshot=t return 'json' end,CRC=function() return '123' end}
timer={active={},fail=false,silent=false}
function timer.Create(name,seconds,count,fn) if timer.fail then error('timer_error') end if not timer.silent then timer.active[name]=fn end end
function timer.Exists(name) return timer.active[name]~=nil end
function timer.Remove(name) timer.active[name]=nil end
function timer.Fire(name) local fn=timer.active[name] timer.active[name]=nil assert(fn) fn() end
output={}; print=function(s) output[#output+1]=s end
'''

for label, module in (("Lua 5.1", lua51), ("LuaJIT", luajit21)):
    lua = module.LuaRuntime(unpack_returned_tuples=True)
    lua.execute(setup)
    run = lua.execute
    def rejects(code, message):
        try:
            run(code)
        except Exception as exc:
            assert message in str(exc), str(exc)
            return
        raise AssertionError("Expected " + message)

    run(scripts["snapshot"])
    run("assert(snapshot.playerCount==1 and snapshot.players[1].userId==7 and snapshot.hookEvents==2 and snapshot.hookHandlers==3)")
    run(scripts["hookInspect"])
    run("assert(snapshot.event=='Think' and snapshot.total==2 and #snapshot.entries==1 and snapshot.entries[1].id=='one' and snapshot.omitted==1)")
    run(scripts["chat"])
    run(r'''assert(p.message=='Hello "Veejay" \\ world')''')
    run(scripts["monitorStart"])
    rejects(scripts["monitorStart"], "chat_monitor_already_running")
    run("hook.GetTable().PlayerSay['GTerm.ChatMonitor'](p,'hello',false)")
    run(scripts["monitorRead"])
    run("assert(snapshot.items[1].text=='hello' and snapshot.items[1].userId==7 and snapshot.nextSequence==1)")
    run("for i=1,70 do hook.GetTable().PlayerSay['GTerm.ChatMonitor'](p,'x',false) end")
    run(scripts["monitorRead"])
    run("assert(#snapshot.items==4 and snapshot.missed and #GTermChatMonitor.records==64)")
    rejects(scripts["monitorRead"].replace('"123"', '"999"'), "epoch_mismatch")
    run(scripts["monitorStop"])
    run("assert(GTermChatMonitor==nil and not hook.GetTable().PlayerSay['GTerm.ChatMonitor'] and not timer.Exists('GTerm.ChatMonitor.Timeout'))")
    run(scripts["monitorStart"])
    run("timer.Fire('GTerm.ChatMonitor.Timeout');assert(GTermChatMonitor==nil and not hook.GetTable().PlayerSay['GTerm.ChatMonitor'])")
    run(scripts["hold"])
    run("assert(p.frozen and timer.Exists('GTermPlayerHold_7'))")
    rejects(scripts["hold"], "hold_already_active")
    run(scripts["release"])
    run("assert(not p.frozen and not timer.Exists('GTermPlayerHold_7'))")
    rejects(scripts["release"], "no_owned_hold")
    run(scripts["hold"])
    run("timer.Fire('GTermPlayerHold_7');assert(not p.frozen)")
    run("p.frozen=true")
    rejects(scripts["hold"], "player_already_frozen")
    run("assert(p.frozen);p.frozen=false;timer.fail=true")
    rejects(scripts["hold"], "timer_error")
    run("assert(not p.frozen and not timer.Exists('GTermPlayerHold_7'));timer.fail=false;timer.silent=true")
    rejects(scripts["hold"], "timer_creation_failed")
    run("assert(not p.frozen);timer.silent=false")
    run(scripts["hold"])
    run("local old=p;old.valid=false;p={valid=true,frozen=true,id=7,UserID=old.UserID,IsFrozen=old.IsFrozen,Freeze=old.Freeze};timer.Fire('GTermPlayerHold_7');assert(p.frozen)")
    run("p.valid=false")
    rejects(scripts["chat"], "player_not_found")
    print(label + ": snapshot, chat, bounded monitor, timeout, release, refusal, cleanup and stale-player checks passed")
