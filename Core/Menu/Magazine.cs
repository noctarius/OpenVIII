﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenVIII
{
    partial class Magazine : SP2
    {
        /// <summary>
        /// Contains Magazines and parts of tutorials; some tutorial images are in Icons.
        /// </summary>
        /// TODO test this.
        public Magazine()
        {
            Props = new List<TexProps>()
            {
                new TexProps("mag{0:00}.tex",20),
                new TexProps("magita.TEX",1),
            };
            TextureStartOffset = 0;
            IndexFilename = "";
            EntriesPerTexture = -1;
            Init();
        }
        /// <summary>
        /// not used in magzine there is no sp2 file.
        /// </summary>
        /// <param name="aw"></param>
        protected override void InitEntries(ArchiveWorker aw = null) { }
    }
}
