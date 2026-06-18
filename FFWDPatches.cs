using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;

namespace OstronautsPerfOpt
{
    // ========================================
    // FFWD / TIME ACCEL OPTIMIZATIONS
    // ========================================
    // During time acceleration (Time.timeScale up to 16x), CrewSim.Update
    // runs every frame with a large deltaTime. StarSystem.UpdateShip runs
    // per ship per frame, calling Ship.Sparks() which spawns particle
    // effects that are invisible during fast-forward travel.
    //
    // Note: UpdateCrewSkills sets STATIC fields (WeaponsSystem.fRangeModGunner,
    // fFuelEfficiencyMod) that reflect per-ship crew state. Throttling it
    // per-ship with a shared static timestamp would corrupt these values
    // (only one ship per frame would update the static). So we do NOT
    // throttle it — the Sparks skip below is the only time-accel visual
    // optimization. UpdateCrewSkills is cheap enough (HasCond checks) to
    // leave alone.
}
