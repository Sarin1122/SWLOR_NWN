﻿using System;
using SWLOR.Game.Server.GameObject;
using SWLOR.Game.Server.Mod.Contracts;
using SWLOR.Game.Server.Service;


namespace SWLOR.Game.Server.Mod
{
    public class ArmorClassMod: IModHandler
    {
        public int ModTypeID => 2;

        public string CanApply(NWPlayer player, NWItem target, params string[] args)
        {
            if (target.CustomAC >= 51) // Actually applies to the PC at 1/3 total, so 51 == 17
                return "You cannot improve that item's AC any further.";

            if (!ItemService.ArmorBaseItemTypes.Contains(target.BaseItemType) &&
                !ItemService.ShieldBaseItemTypes.Contains(target.BaseItemType))
                return "This mod can only be applied to armors and shields.";

            return null;
        }

        public void Apply(NWPlayer player, NWItem target, params string[] args)
        {
            int amount = Convert.ToInt32(args[0]);
            target.CustomAC += amount;
        }

        public string Description(NWPlayer player, NWItem target, params string[] args)
        {
            int amount = Convert.ToInt32(args[0]);
            return "AC +" + amount;
        }
    }
}
