﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kiva
{
    public class SoundfontData
    {
        public string path = null;
        public int srcb = -1;
        public int srcp = -1;
        public int desb = 0;
        public int desp = -1;
        public bool xgdrums = false;
        public bool preload = true;
        public bool enabled;

        public Dictionary<string, string> otherParams = new Dictionary<string, string>();

        public SoundfontData(bool sfz = false)
        {
            if (sfz)
            {
                srcb = 0;
                srcp = 0;
                desp = 0;
            }
        }
    }

    public class SoundfontSettings
    {
        public SoundfontSettings()
        {

        }

        bool justWrote = false;

        public event Action<string> OnSave;

        public event Action<bool> SoundfontsUpdated;

        public void ParseFile(string[] lines)
        {
            if (justWrote)
            {
                justWrote = false;
                return;
            }

            List<SoundfontData> fonts = new List<SoundfontData>();
            SoundfontData currfont = new SoundfontData();

            int lineno = 0;


            foreach (string _line in lines)
            {
                var line = _line.Trim(' ', '\r', '\t');
                lineno++;

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                if (line == "sf.start")
                {
                    currfont = new SoundfontData();
                    continue;
                }

                if (line == "sf.end")
                {
                    if (currfont.path == null)
                    {
                        throw new Exception("Missing filename at line " + lineno);
                    }
                    fonts.Add(currfont);
                    continue;
                }

                if (!line.StartsWith("sf."))
                {
                    throw new Exception("Invalid line " + lineno);
                }

                int idx = line.IndexOf(" = ");
                if (idx < 4)
                {
                    throw new Exception("Invalid instruction at line " + lineno);
                }

                string instr = line.Substring(3, idx - 3);
                string idata = line.Substring(idx + 3);

                switch (instr)
                {
                    case "path": currfont.path = idata; break;
                    case "enabled": currfont.enabled = idata != "0"; break;
                    case "srcb": currfont.srcb = int.Parse(idata); break;
                    case "srcp": currfont.srcp = int.Parse(idata); break;
                    case "desb": currfont.desb = int.Parse(idata); break;
                    case "desp": currfont.desp = int.Parse(idata); break;
                    case "xgdrums": currfont.xgdrums = idata != "0"; break;
                    case "preload": currfont.preload = idata != "0"; break;

                    default:
                        currfont.otherParams.Add(instr, idata); break;
                }
            }

            var fontarr = fonts.ToArray();
            Soundfonts = fontarr;
            SoundfontsUpdated?.Invoke(true);
        }

        public void SaveList()
        {
            List<string> lines = new List<string>();

            lines.Add("// Generated by Kiva");
            lines.Add("");

            int i = 1;
            foreach (var sf in Soundfonts)
            {
                lines.Add("// SoundFont n°" + (i++));
                lines.Add("sf.start");
                lines.Add("sf.path = " + sf.path);
                lines.Add("sf.enabled = " + (sf.enabled ? 1 : 0));
                lines.Add("sf.preload = " + (sf.preload ? 1 : 0));
                lines.Add("sf.srcb = " + sf.srcb);
                lines.Add("sf.srcp = " + sf.srcp);
                lines.Add("sf.desb = " + sf.desb);
                lines.Add("sf.desp = " + sf.desp);
                lines.Add("sf.xgdrums = " + (sf.xgdrums ? 1 : 0));
                foreach (var k in sf.otherParams.Keys)
                {
                    lines.Add("sf." + k + " = " + sf.otherParams[k]);
                }
                lines.Add("sf.end");
                lines.Add("");
            }

            lines.Add("// Generated by Kiva");

            var s = "";
            foreach (var l in lines) s += l + "\n";

            OnSave?.Invoke(s);
            SoundfontsUpdated?.Invoke(false);
        }

        public SoundfontData[] Soundfonts { get; set; } = new SoundfontData[0];
    }
}
