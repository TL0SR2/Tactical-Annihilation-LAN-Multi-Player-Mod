using System;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Mirrors vanilla DataUtils skirmish eco / AI-intel tables (game update).
    /// Pure — no Unity. Used by lobby DTO defaults, Host seat edits, and battle start mapping.
    /// </summary>
    public static class SkirmishSeatEconomy
    {
        /// <summary>PlayerControl.Custom</summary>
        public const int ControllerCustom = 6;

        /// <summary>PlayerControl.Human</summary>
        public const int ControllerHuman = 0;

        /// <summary>Vanilla DataUtils.SkirmishResMulOptions.</summary>
        public static readonly float[] ResMulOptions =
        {
            0.5f, 0.7f, 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.8f, 2f, 2.5f, 3f
        };

        /// <summary>Vanilla DataUtils.SkirmishAIIntelOptions.</summary>
        public static readonly float[] AiIntelOptions =
        {
            0.2f, 0.4f, 0.7f, 0.9f, 1f
        };

        public const float DefaultResPercent = 1f;
        public const float DefaultAiIntelligence = 0.7f; // AI_Normal
        /// <summary>Vanilla SetupForSkirmish Custom branch when ai_interlligence ≤ 0.</summary>
        public const float VanillaCustomAiIntelligenceFallback = 1f;

        /// <summary>
        /// Authoritative values stamped onto SGS_Player so GS_Battle.SetupForSkirmish
        /// actually applies them to Player.res_mul / Player.ai_intelligence
        /// (preset AI cases ignore SGS floats and call GetAIDiff*).
        /// </summary>
        public struct SgsControlStamp
        {
            public int controller;
            public float resPercent;
            public float aiIntelligence;
        }

        /// <summary>Vanilla DataUtils.GetAIDiffResMul for AI_Beginner..AI_Crazy.</summary>
        public static float GetPresetResMul(int controller)
        {
            switch (controller)
            {
                case 1: return 0.5f; // Beginner
                case 2: return 0.7f; // Easy
                case 3: return 1f;   // Normal
                case 4: return 1.2f; // Hard
                case 5: return 1.4f; // Crazy
                default: return DefaultResPercent;
            }
        }

        /// <summary>Vanilla DataUtils.GetAIDiffIntelligence for AI_Beginner..AI_Crazy.</summary>
        public static float GetPresetAiIntelligence(int controller)
        {
            switch (controller)
            {
                case 1: return 0.2f;
                case 2: return 0.4f;
                case 3: return 0.7f;
                case 4: return 0.9f;
                case 5: return 1f;
                default: return DefaultAiIntelligence;
            }
        }

        public static int IndexOfResMul(float value) => NearestIndex(ResMulOptions, value);

        public static int IndexOfAiIntel(float value) => NearestIndex(AiIntelOptions, value);

        public static float ResMulAt(int index)
        {
            if (index < 0 || index >= ResMulOptions.Length)
                return DefaultResPercent;
            return ResMulOptions[index];
        }

        public static float AiIntelAt(int index)
        {
            if (index < 0 || index >= AiIntelOptions.Length)
                return DefaultAiIntelligence;
            return AiIntelOptions[index];
        }

        public static bool IsPresetAiController(int controller) =>
            controller >= 1 && controller <= 5;

        public static bool IsCustomController(int controller) =>
            controller == ControllerCustom;

        public static bool ApproxEqual(float a, float b) =>
            Math.Abs(a - b) < 0.001f;

        /// <summary>Fill seat eco fields from a preset AI controller (1..5).</summary>
        public static void ApplyPresetToSeat(LobbySeatDto seat, int controller)
        {
            if (seat == null)
                return;
            seat.resPercent = GetPresetResMul(controller);
            seat.aiIntelligence = GetPresetAiIntelligence(controller);
        }

        public static void EnsureDefaults(LobbySeatDto seat)
        {
            if (seat == null)
                return;
            if (seat.resPercent <= 0f)
                seat.resPercent = DefaultResPercent;
            if (seat.aiIntelligence <= 0f)
            {
                if (IsPresetAiController(seat.controller))
                    seat.aiIntelligence = GetPresetAiIntelligence(seat.controller);
                else if (IsCustomController(seat.controller))
                    seat.aiIntelligence = DefaultAiIntelligence;
                else
                    seat.aiIntelligence = 0f; // Human unused
            }
        }

        /// <summary>
        /// Map lobby seat → SGS controller/floats that SetupForSkirmish will honor.
        /// Human: controller=Human, res_percent applied when &gt; 0.
        /// AI: if Host eco/intel matches a preset, keep that preset; otherwise force Custom
        /// so Setup reads SGS floats (preset cases call GetAIDiff* and ignore SGS).
        /// </summary>
        public static SgsControlStamp ResolveForStart(LobbySeatDto seat, bool humanSeated)
        {
            var res = seat != null && seat.resPercent > 0f
                ? seat.resPercent
                : DefaultResPercent;

            if (humanSeated)
            {
                return new SgsControlStamp
                {
                    controller = ControllerHuman,
                    resPercent = res,
                    aiIntelligence = 0f
                };
            }

            var intel = seat != null && seat.aiIntelligence > 0f
                ? seat.aiIntelligence
                : DefaultAiIntelligence;
            var c = seat != null ? seat.controller : 3;
            if (c <= 0 || c > ControllerCustom)
                c = 3; // AI_Normal fallback

            if (IsCustomController(c))
            {
                return new SgsControlStamp
                {
                    controller = ControllerCustom,
                    resPercent = res,
                    aiIntelligence = intel
                };
            }

            if (IsPresetAiController(c))
            {
                var presetRes = GetPresetResMul(c);
                var presetIntel = GetPresetAiIntelligence(c);
                if (!ApproxEqual(res, presetRes) || !ApproxEqual(intel, presetIntel))
                {
                    // Host overrode eco and/or intel — must use Custom path.
                    return new SgsControlStamp
                    {
                        controller = ControllerCustom,
                        resPercent = res,
                        aiIntelligence = intel
                    };
                }

                return new SgsControlStamp
                {
                    controller = c,
                    resPercent = presetRes,
                    aiIntelligence = presetIntel
                };
            }

            return new SgsControlStamp
            {
                controller = ControllerCustom,
                resPercent = res,
                aiIntelligence = intel
            };
        }

        /// <summary>
        /// Effective Player.res_mul after Setup: prefer stamped SGS (&gt;0); preset with -1 → GetAIDiff*; else 1.
        /// </summary>
        public static float ResolveEffectiveResMul(float sgsResPercent, int controller)
        {
            if (sgsResPercent > 0f)
                return sgsResPercent;
            if (IsPresetAiController(controller))
                return GetPresetResMul(controller);
            return DefaultResPercent;
        }

        /// <summary>
        /// Effective Player.ai_intelligence: prefer SGS (&gt;0); preset → GetAIDiff*; Custom empty → 1f (vanilla).
        /// </summary>
        public static float ResolveEffectiveAiIntelligence(float sgsAiIntelligence, int controller)
        {
            if (sgsAiIntelligence > 0f)
                return sgsAiIntelligence;
            if (IsPresetAiController(controller))
                return GetPresetAiIntelligence(controller);
            if (IsCustomController(controller))
                return VanillaCustomAiIntelligenceFallback;
            return DefaultAiIntelligence;
        }

        private static int NearestIndex(float[] options, float value)
        {
            if (options == null || options.Length == 0)
                return 0;
            var best = 0;
            var bestDist = Math.Abs(options[0] - value);
            for (var i = 1; i < options.Length; i++)
            {
                var d = Math.Abs(options[i] - value);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }
    }
}
