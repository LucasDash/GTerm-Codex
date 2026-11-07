using Newtonsoft.Json.Linq;

namespace GTerm.MCP
{
    /// <summary>Explicit, bounded, short-lived server chat observation; text is untrusted.</summary>
    internal static class ServerChatMonitor
    {
        private const string Prefix = "local state=_G.GTermChatMonitor; ";
        private const string Check = "if not istable(state) or state.epoch~=epoch then error('chat_monitor_not_found_or_epoch_mismatch',0) end; ";

        private static int IntArg(JObject? args, string key, int minimum, int maximum, int? fallback = null)
        {
            JToken? token = args?[key];
            if (token == null && fallback.HasValue) return fallback.Value;
            if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out int value) || value < minimum || value > maximum)
                throw new ArgumentException($"{key} must be an integer from {minimum} to {maximum}.");
            return value;
        }

        internal static string Build(JObject? args)
        {
            string action = args?["action"]?.Type == JTokenType.String ? args["action"]!.ToString() : "";
            if (action == "start")
            {
                int seconds = IntArg(args, "seconds", 10, 120);
                return Prefix + $"local seconds={seconds}; " + @"
if state~=nil then error('chat_monitor_already_running',0) end
local hookName='GTerm.ChatMonitor'; local timerName='GTerm.ChatMonitor.Timeout'
local existing=hook.GetTable().PlayerSay
if (existing and existing[hookName]) or timer.Exists(timerName) then error('chat_monitor_name_in_use',0) end
state={epoch=util.CRC(tostring(SysTime())..':'..tostring(CurTime())..':'..tostring({})),sequence=0,records={}}
local own=state
local function cleanup()
 if _G.GTermChatMonitor~=own then return end
 local hooks=hook.GetTable().PlayerSay
 if hooks and hooks[hookName]==own.callback then hook.Remove('PlayerSay',hookName) end
 timer.Remove(timerName); _G.GTermChatMonitor=nil
end
own.callback=function(p,text,teamChat)
 if not IsValid(p) or not p:IsPlayer() or p:IsBot() or not isstring(text) then return end
 own.sequence=own.sequence+1
 local safeText=text; local truncated=#text>160
 if truncated then safeText='' end
 local valid,encoded=pcall(util.TableToJSON,{safeText})
 if not valid or not isstring(encoded) then safeText=''; truncated=true end
 own.records[#own.records+1]={sequence=own.sequence,userId=p:UserID(),time=CurTime(),text=safeText,truncated=truncated,team=teamChat and true or false}
 if #own.records>64 then table.remove(own.records,1) end
end
_G.GTermChatMonitor=own
local ok,err=pcall(function()
 hook.Add('PlayerSay',hookName,own.callback)
 timer.Create(timerName,seconds,1,cleanup)
 if not timer.Exists(timerName) then error('chat_monitor_timer_failed',0) end
end)
if not ok then cleanup(); error(err,0) end
print('GTERM_CHAT_MONITOR '..util.TableToJSON({action='started',epoch=own.epoch,seconds=seconds,sequence=0}))";
            }

            if (action != "read" && action != "stop") throw new ArgumentException("action must be start, read or stop.");
            if (args?["epoch"]?.Type != JTokenType.String) throw new ArgumentException("epoch is required and must be a decimal string from start.");
            string epoch = args["epoch"]!.ToString();
            if (epoch.Length < 1 || epoch.Length > 16 || !epoch.All(char.IsAsciiDigit))
                throw new ArgumentException("epoch must be a decimal string from start.");
            string prefix = $"local epoch={GTermSentinels.LuaLiteral(epoch)}; " + Prefix + Check;
            if (action == "stop")
                return prefix + @"local name='GTerm.ChatMonitor'; local hooks=hook.GetTable().PlayerSay; if hooks and hooks[name]==state.callback then hook.Remove('PlayerSay',name) end; timer.Remove('GTerm.ChatMonitor.Timeout'); _G.GTermChatMonitor=nil; print('GTERM_CHAT_MONITOR '..util.TableToJSON({action='stopped',epoch=epoch,sequence=state.sequence}))";

            int after = IntArg(args, "after_sequence", 0, int.MaxValue, 0);
            int limit = IntArg(args, "limit", 1, 4, 4);
            return prefix + $"local after={after}; local limit={limit}; " + @"
if after>state.sequence then error('chat_cursor_ahead',0) end
local items={}; local cursor=after; local oldest=state.records[1] and state.records[1].sequence or state.sequence+1
for _,item in ipairs(state.records) do if item.sequence>after and #items<limit then items[#items+1]=item; cursor=item.sequence end end
print('GTERM_CHAT_MONITOR '..util.TableToJSON({action='read',epoch=epoch,items=items,nextSequence=cursor,latestSequence=state.sequence,missed=after<oldest-1}))";
        }
    }
}
