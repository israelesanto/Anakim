#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnakimSuite.AnakimAccessProvider;

public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db   = (IAnakimAccessProvider)globals.Db;
        var args = (IDictionary<string, object>)globals.Args;

        // 0) Prefer: current_user_id injected by Orchestrator
        long? userId = TryGetLong(args, "current_user_id");

        // 1) Fallback: token (Authorization Bearer / *_at cookie / args.access_token)
        if (userId is null)
        {
            var token = TryGetAccessToken(globals, args);
            if (string.IsNullOrWhiteSpace(token))
                return new { ok = false, error = "missing_access_token", detail = "Orchestrator should inject current_user_id or forward Authorization/Cookie." };

            userId = TryGetUserIdFromJwt(token);
            if (userId is null)
                return new { ok = false, error = "invalid_token_payload", detail = "Could not read 'sub' from JWT payload." };
        }

        // Read/validate (EN only)
        string targetName   = GetStr(args, "target_name");
        string targetPhone  = GetStr(args, "target_phone");
        string locationText = GetStr(args, "location_text");
        string title        = GetStr(args, "title");
        string body         = GetStr(args, "body");
        int?   ratingOpt    = GetInt(args, "rating");
        string categoryStr  = GetStr(args, "category");
        bool   isAnonymous  = GetBool(args, "is_anonymous") ?? true;
        int    status       = MapStatus(GetStr(args, "status"));     // 1=pending, 2=published, 3=rejected
        int    category     = MapCategory(categoryStr);              // 1=boss, 2=cleaner_helper

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(targetName)) errors.Add("field 'target_name' is required");
        if (string.IsNullOrWhiteSpace(title))      errors.Add("field 'title' is required");
        if (string.IsNullOrWhiteSpace(body))       errors.Add("field 'body' is required");
        if (ratingOpt is null)                     errors.Add("field 'rating' is required");
        if (category == 0)                         errors.Add("field 'category' must be 'boss' or 'cleaner_helper'");
        if (errors.Count > 0) return new { ok = false, errors };

        int rating = Math.Clamp(ratingOpt!.Value, 1, 5);
        if (!string.IsNullOrWhiteSpace(targetPhone))
            targetPhone = Regex.Replace(targetPhone, @"\s+", "");

        DateTime? publishedAt = (status == 2) ? DateTime.UtcNow : (DateTime?)null;

        // INSERT
        var insertParams = new Dictionary<string, object?> {
            ["author_user_id"] = userId!.Value,
            ["is_anonymous"]   = isAnonymous,
            ["category"]       = category,
            ["target_name"]    = targetName,
            ["target_phone"]   = string.IsNullOrWhiteSpace(targetPhone) ? null : targetPhone,
            ["location_text"]  = string.IsNullOrWhiteSpace(locationText) ? null : locationText,
            ["rating"]         = rating,
            ["title"]          = title,
            ["body"]           = body,
            ["status"]         = status,
            ["published_at"]   = publishedAt
        };

        var insertSql = @"
            INSERT INTO public.rh_review
                (author_user_id, is_anonymous, category, target_name, target_phone, location_text,
                 rating, title, body, status, published_at)
            VALUES
                (@author_user_id, @is_anonymous, @category, @target_name, @target_phone, @location_text,
                 @rating, @title, @body, @status, @published_at)
            RETURNING review_id;
        ";

        var insertedId = await db.ExecuteScalarAsync(insertSql, insertParams);

        // LIST (JSON via SQL, sem depender de métodos extras)
        var listSql = @"
            SELECT COALESCE(
                     jsonb_agg(to_jsonb(t) ORDER BY t.created_at DESC),
                     '[]'::jsonb
                   )::text
            FROM (
                SELECT review_id, author_user_id, is_anonymous, category, target_name, target_phone,
                       location_text, rating, title, body, status, created_at, updated_at, published_at
                FROM public.rh_review
                WHERE author_user_id = @uid
                ORDER BY created_at DESC
            ) t;
        ";
        var jsonTextObj = await db.ExecuteScalarAsync(listSql, new Dictionary<string, object> { ["@uid"] = userId.Value });
        var jsonText = jsonTextObj?.ToString() ?? "[]";
        object? items;
        try { items = JsonSerializer.Deserialize<object>(jsonText); } catch { items = new object[0]; }

        return new { ok = true, review_id = insertedId, items };
    }

    // --- helpers ---
    private static string GetStr(IDictionary<string, object> args, string key)
        => args.TryGetValue(key, out var v) && v is not null ? (v.ToString() ?? "").Trim().Trim('"') : "";
    private static int? GetInt(IDictionary<string, object> args, string key)
    { if (!args.TryGetValue(key, out var v) || v is null) return null;
      var s = v.ToString();
      if (int.TryParse(s, out var i)) return i;
      if (double.TryParse(s, out var d)) return (int)Math.Round(d, MidpointRounding.AwayFromZero);
      return null; }
    private static bool? GetBool(IDictionary<string, object> args, string key)
    { if (!args.TryGetValue(key, out var v) || v is null) return null;
      var s = (v.ToString() ?? "").Trim().Trim('"').ToLowerInvariant();
      if (s is "1" or "true" or "t" or "yes" or "y") return true;
      if (s is "0" or "false" or "f" or "no" or "n") return false;
      return null; }
    private static long? TryGetLong(IDictionary<string, object> args, string key)
    { if (!args.TryGetValue(key, out var v) || v is null) return null;
      return long.TryParse(v.ToString(), out var l) ? l : null; }
    private static int MapCategory(string s)
    { if (int.TryParse(s, out var n) && (n==1||n==2)) return n;
      s=(s??"").Trim().ToLowerInvariant();
      if (s=="boss") return 1;
      if (s=="cleaner_helper"||s=="cleaner-helper"||s=="cleaner"||s=="helper") return 2;
      return 0; }
    private static int MapStatus(string s)
    { if (int.TryParse(s, out var n) && n>=1&&n<=3) return n;
      s=(s??"").Trim().ToLowerInvariant();
      if (s=="published") return 2;
      if (s=="rejected")  return 3;
      return 1; }
    private static string? TryGetAccessToken(dynamic globals, IDictionary<string, object> args)
    {
        if (args.TryGetValue("access_token", out var at) && at is not null) return at.ToString();
        try
        {
            var headers = (IDictionary<string, object>)globals.Headers;
            if (headers != null && headers.TryGetValue("Authorization", out var auth) && auth is not null)
            { var s = auth.ToString(); if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return s.Substring("Bearer ".Length).Trim(); }
            if (headers != null && headers.TryGetValue("Cookie", out var ck) && ck is not null)
            {
                var cookie = ck.ToString() ?? "";
                foreach (var part in cookie.Split(';'))
                {
                    var kv = part.Trim().Split('=', 2);
                    if (kv.Length == 2 && kv[0].EndsWith("_at", StringComparison.OrdinalIgnoreCase))
                        return kv[1];
                }
            }
        } catch { }
        return null;
    }
    private static long? TryGetUserIdFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("sub", out var subProp))
            {
                var s = subProp.GetString();
                if (long.TryParse(s, out var l)) return l;
            }
        } catch { }
        return null;
    }
    private static string Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        var bytes = Convert.FromBase64String(s);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

// ⚠️ DO NOT call Run here.
return new ScriptHandler();
