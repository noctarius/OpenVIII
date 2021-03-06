﻿using Microsoft.Xna.Framework;
using System;

namespace OpenVIII
{
    public partial class Module_main_menu_debug
    {

        #region Classes

        private partial class IGM_Junction
        {

            #region Classes

            private class IGMData_Mag_ST_A_D_Slots : IGMData_Slots<Saves.CharacterData>
            {

                #region Constructors

                public IGMData_Mag_ST_A_D_Slots() : base(5, 2, new IGMDataItem_Box(pos: new Rectangle(0, 414, 840, 216)), 1, 5)
                {

                }

                #endregion Constructors

                #region Methods

                public override void BackupSetting() => SetPrevSetting(Memory.State.Characters[Character].Clone());

                public override void CheckMode(bool cursor = true) =>
                    CheckMode(0, Mode.Mag_ST_A, Mode.Mag_ST_D,
                        InGameMenu_Junction != null && (InGameMenu_Junction.GetMode() == Mode.Mag_ST_A || InGameMenu_Junction.GetMode() == Mode.Mag_ST_D),
                        InGameMenu_Junction != null && (InGameMenu_Junction.GetMode() == Mode.Mag_Pool_ST_A || InGameMenu_Junction.GetMode() == Mode.Mag_Pool_ST_D),
                        cursor);

                public override void Inputs_CANCEL()
                {
                    base.Inputs_CANCEL();
                    InGameMenu_Junction.SetMode(Mode.TopMenu_Junction);
                }

                public override void Inputs_Left()
                {
                    base.Inputs_Left();
                    PageLeft();
                }

                public override void Inputs_OKAY()
                {
                    if (!BLANKS[CURSOR_SELECT])
                    {
                        base.Inputs_OKAY();
                        BackupSetting();
                        InGameMenu_Junction.SetMode(CURSOR_SELECT == 0 ? Mode.Mag_Pool_ST_A : Mode.Mag_Pool_ST_D);
                    }
                }

                public override void Inputs_Right()
                {
                    base.Inputs_Right();
                    PageRight();
                }

                public override void Inputs_Square()
                {
                    skipdata = true;
                    base.Inputs_Square();
                    skipdata = false;
                    if (Contents[CURSOR_SELECT] == Kernel_bin.Stat.None)
                    {
                        Memory.State.Characters[Character].Stat_J[Contents[CURSOR_SELECT]] = 0;
                        InGameMenu_Junction.ReInit();
                    }
                }

                public override void ReInit()
                {
                    if (Memory.State.Characters != null)
                    {
                        base.ReInit();
                        FillData(Icons.ID.Icon_Status_Attack, Kernel_bin.Stat.ST_Atk, Kernel_bin.Stat.ST_Def_1);
                    }
                }

                public override void UndoChange()
                {
                    //override this use it to take value of prevSetting and restore the setting unless default method works
                    if (GetPrevSetting() != null)
                    {
                        Memory.State.Characters[Character] = GetPrevSetting().Clone();
                    }
                }

                protected override void AddEventListener()
                {
                    if (!eventAdded)
                    {
                        IGMData_Mag_Pool.SlotConfirmListener += ConfirmChangeEvent;
                        IGMData_Mag_Pool.SlotReinitListener += ReInitEvent;
                        IGMData_Mag_Pool.SlotUndoListener += UndoChangeEvent;
                    }
                    base.AddEventListener();
                }

                protected override void Init() => base.Init();

                protected override void InitShift(int i, int col, int row)
                {
                    base.InitShift(i, col, row);
                    SIZE[i].Inflate(-30, -6);
                    SIZE[i].Y -= row * 2;
                }
                protected override void PageLeft() => InGameMenu_Junction.SetMode(Mode.Mag_Stat);

                protected override void PageRight() => InGameMenu_Junction.SetMode(Mode.Mag_EL_A);

                protected override void SetCursor_select(int value)
                {
                    if (value != GetCursor_select())
                    {
                        base.SetCursor_select(value);
                        CheckMode();
                        IGMData_Mag_Pool.StatEventListener?.Invoke(this, Contents[CURSOR_SELECT]);
                    }
                }

                protected override bool Unlocked(byte pos)
                {
                    switch (pos)
                    {
                        case 0:
                            return unlocked.Contains(Kernel_bin.Abilities.ST_Atk_J);
                        case 1:
                            return unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx1) ||
                                unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx2) ||
                                unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx4);
                        case 2:
                            return unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx2) ||
                                unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx4);
                        case 3:
                        case 4:
                            return unlocked.Contains(Kernel_bin.Abilities.ST_Def_Jx4);
                        default:
                            return false;
                    }
                }

                private void ConfirmChangeEvent(object sender, Mode e) => ConfirmChange();

                private void ReInitEvent(object sender, Mode e) => ReInit();

                private void UndoChangeEvent(object sender, Mode e) => UndoChange();

                #endregion Methods

            }

            #endregion Classes

        }

        #endregion Classes

    }
}