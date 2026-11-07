using Newtonsoft.Json.Linq;

namespace GTerm.MCP
{
    /// <summary>Bounded, opt-in-at-call-time operations using the existing server Lua executor.</summary>
    internal static class ServerOperations
    {
        private const string FindPlayer = "local p; for _,v in ipairs(player.GetHumans()) do if v:UserID()==uid then p=v break end end; if not IsValid(p) then error('player_not_found',0) end; ";

        internal static string Snapshot() => @"
local users=player.GetHumans()
table.sort(users,function(a,b) return a:UserID()<b:UserID() end)
local out={map=game.GetMap(),time=CurTime(),players={},playerCount=#users,playersTruncated=#users>8,entities=#ents.GetAll(),hookEvents=0,hookHandlers=0}
for i=1,math.min(#users,8) do
 local p=users[i] local pos=p:GetPos() local vel=p:GetVelocity()
 out.players[#out.players+1]={userId=p:UserID(),entity=p:EntIndex(),alive=p:Alive(),health=p:Health(),maxHealth=p:GetMaxHealth(),frozen=p:IsFrozen(),onGround=p:IsOnGround(),moveType=p:GetMoveType(),inVehicle=p:InVehicle(),position={x=pos.x,y=pos.y,z=pos.z},speed=vel:Length()}
end
for _,handlers in pairs(hook.GetTable()) do if istable(handlers) then out.hookEvents=out.hookEvents+1 for _ in pairs(handlers) do out.hookHandlers=out.hookHandlers+1 end end end
print('GTERM_SERVER_SNAPSHOT '..util.TableToJSON(out))";

        internal static string Hooks(JObject? args)
        {
            if (args?["event"]?.Type != JTokenType.String) throw new ArgumentException("event must be a Lua hook event name.");
            string eventName = args["event"]!.ToString();
            if (eventName.Length < 1 || eventName.Length > 64 || !eventName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("event must contain only ASCII letters, digits or underscores (1-64 characters).");
            int limit = 8;
            if (args["limit"] != null && (args["limit"]!.Type != JTokenType.Integer || !int.TryParse(args["limit"]!.ToString(), out limit) || limit < 1 || limit > 16))
                throw new ArgumentException("limit must be an integer from 1 to 16.");
            return "local event=" + GTermSentinels.LuaLiteral(eventName) + "; local limit=" + limit + @";
local handlers=hook.GetTable()[event]; local out={event=event,total=0,entries={}}
if istable(handlers) then
 for id,fn in pairs(handlers) do
  out.total=out.total+1
  if isstring(id) and isfunction(fn) and #out.entries<limit then
   local info=debug.getinfo(fn,'S') or {}; local src=info.short_src or '?'
   if #src>128 then src='[long source]' end
   if #id>64 then id='[long identifier]' end
   out.entries[#out.entries+1]={id=id,source=src,kind=info.what or '?'}
  end
 end
end
table.sort(out.entries,function(a,b) return a.id<b.id end)
out.omitted=out.total-#out.entries
print('GTERM_LUA_HOOKS '..util.TableToJSON(out))";
        }
        internal static int UserId(JObject? args)
        {
            JToken? token = args?["user_id"];
            if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out int id) || id < 1)
                throw new ArgumentException("user_id must be a positive integer from the current server snapshot.");
            return id;
        }

        internal static string Chat(JObject? args)
        {
            int id = UserId(args);
            if (args?["message"]?.Type != JTokenType.String) throw new ArgumentException("message must be a string.");
            string message = args["message"]!.ToString();
            if (string.IsNullOrWhiteSpace(message) || message.Length > 160 || message.Any(char.IsControl))
                throw new ArgumentException("message must contain 1-160 characters and no control characters.");
            return $"local uid={id}; " + FindPlayer + $"p:ChatPrint({GTermSentinels.LuaLiteral(message)}); print('GTERM_CHAT_SENT user_id='..uid)";
        }

        internal static string Hold(JObject? args)
        {
            int id = UserId(args);
            string action = args?["action"]?.Type == JTokenType.String ? args["action"]!.ToString() : "hold";
            if (action != "hold" && action != "release") throw new ArgumentException("action must be hold or release.");
            int seconds = 0;
            if (action == "hold")
            {
                JToken? token = args?["seconds"];
                if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out seconds) || seconds < 1 || seconds > 15)
                    throw new ArgumentException("seconds must be an integer from 1 to 15.");
            }
            string prefix = $"local uid={id}; " + FindPlayer;
            string common = @"if _G.GTermPlayerHolds~=nil and not istable(_G.GTermPlayerHolds) then error('lease_store_invalid',0) end; local leases=_G.GTermPlayerHolds or {}; _G.GTermPlayerHolds=leases; local key=tostring(uid); local name='GTermPlayerHold_'..key; ";
            if (action == "release")
                return prefix + common + @"local l=leases[key]; if not l or l.player~=p then error('no_owned_hold',0) end; timer.Remove(name); leases[key]=nil; if IsValid(p) and p:UserID()==uid and p:IsFrozen() then p:Freeze(false) end; print('GTERM_HOLD_RELEASED user_id='..uid)";
            return prefix + common + $"local seconds={seconds}; " + @"
if leases[key] or timer.Exists(name) then error('hold_already_active',0) end
if p:IsFrozen() then error('player_already_frozen',0) end
local lease={player=p}; leases[key]=lease
local ok,err=pcall(function()
 timer.Create(name,seconds,1,function()
  if leases[key]~=lease then return end
  leases[key]=nil
  if IsValid(p) and p:UserID()==uid and p:IsFrozen() then p:Freeze(false) end
 end)
 if not timer.Exists(name) then error('timer_creation_failed',0) end
 p:Freeze(true)
 if not p:IsFrozen() then error('freeze_failed',0) end
end)
if not ok then timer.Remove(name); leases[key]=nil; if IsValid(p) and p:UserID()==uid and p:IsFrozen() then p:Freeze(false) end; error(err,0) end
print('GTERM_HOLD_STARTED user_id='..uid..' seconds='..seconds)";
        }
    }
}
