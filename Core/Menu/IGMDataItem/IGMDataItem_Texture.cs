﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenVIII
{
    public partial class Module_main_menu_debug
    {
        #region Classes

        private class IGMDataItem_Texture : IGMDataItem
        {
            public Texture2D Data { get; set; }

            public IGMDataItem_Texture(Texture2D data, Rectangle? pos = null) : base(pos) => this.Data = data;

            public override void Draw()
            {
                if (Enabled)
                {
                    Memory.spriteBatch.Draw(Data, Pos, null, base.Color * fade);//4
                }
            }
        }
        #endregion Classes
    }
}