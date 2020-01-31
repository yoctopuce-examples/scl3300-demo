using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TestInclinometer
{
    class MurataInclinometer : Inclinometer
    {
        private YSpiPort spiPort;
        private bool _chip_ready = false;

        private const string SET_MODE_4 = "B4000338";
        private const string SET_ANGLES = "B0001F6F";
        private const string READ_STATUS = "180000E5";
        private const string READ_ID = "40000091";
        private const string READ_ANGLE_X = "240000C7";
        private const string READ_ANGLE_Y = "280000CD";
        private const string READ_ANGLE_Z = "2C0000CB";

        private class Frame
        {
            public string hex;
            public uint crc;
            public uint data;
            public uint rs;
            public uint addr;
            public uint rd;
            public bool crc_error;

            public Frame(string hexstring)
            {
                UInt32 uInt32;

                hex = hexstring;
                uInt32 = Convert.ToUInt32(hex, 16);
                crc = uInt32 & 0xff;
                data = (uInt32 >> 8) & 0xffff;
                rs = (uInt32 >> 24) & 0x3;
                addr = (uInt32 >> 26) & 0x1f;
                rd = (uInt32 >> 31);
                uint crc_check = CalculateCRC(uInt32);
                crc_error = (crc_check != crc);
            }

            public override string ToString()
            {
                string res = String.Format("OP={0:X} ADDR={1:X} RS={2:X} data={3:X04}", rd, addr, rs, data);
                if (crc_error) res += " CRC-Err!";
                return res;
            }

            // Calculate CRC for 24 MSB's of the 32 bit dword
            // (8 LSB's are the CRC field and are not included in CRC calculation)
            uint CalculateCRC(uint Data)
            {
                byte BitIndex;
                byte BitValue;
                byte CRC;
                CRC = 0xFF;
                for (BitIndex = 31; BitIndex > 7; BitIndex--)
                {
                    BitValue = (byte)((Data >> BitIndex) & 0x01);
                    CRC = CRC8(BitValue, CRC);
                }

                CRC = (byte)~CRC;
                return CRC;
            }

            static byte CRC8(byte BitValue, byte CRC)
            {
                byte Temp;
                Temp = (byte)(CRC & 0x80);
                if (BitValue == 0x01)
                {
                    Temp ^= 0x80;
                }

                CRC <<= 1;
                if (Temp > 0)
                {
                    CRC ^= 0x1D;
                }

                return CRC;
            }
        }

        private bool SendAndReceive(string[] commands, out Frame[] result)
        {
            int i;
            bool success = true;

            spiPort.reset();
            for(i = 0; i < commands.Length; i++)
            {
                spiPort.writeHex(commands[i]);
            }
            // append an extra command to read result of the last command
            spiPort.writeHex(READ_ID);
            // wait for the result of all commands to come
            int expectedLength = commands.Length * 4;
            while (spiPort.read_avail() < expectedLength)
            {
                string errmsg = "";
                YAPI.Sleep(3, ref errmsg);
            }
            string hexstr = spiPort.readHex(2*expectedLength);

            // Parse result
            result = new Frame[commands.Length];
            for (i = 0; i < commands.Length; i++)
            {
                Frame query = new Frame(commands[i]);
                result[i] = new Frame(hexstr.Substring(8 + 8 * i, 8));
                if(result[i].crc_error || result[i].addr != query.addr)
                {
                    success = false;
                }
                _chip_ready = (result[i].rs == 1);
            }

            return success;
        }

        public override bool Setup()
        {
            spiPort = YSpiPort.FirstSpiPort();
            if (spiPort == null) {
                Console.WriteLine("No Yocto-Spi detected");
                return false;
            }

            YModule module = spiPort.get_module();
            string errmsg = "";
            string serialNumber = module.get_serialNumber();
            YPowerOutput powerOutput = YPowerOutput.FindPowerOutput(serialNumber + ".powerOutput");

            powerOutput.set_voltage(YPowerOutput.VOLTAGE_OUT3V3);
            spiPort.set_voltageLevel(YSpiPort.VOLTAGELEVEL_TTL3V);
            spiPort.set_spiMode("2000000,0,msb");
            spiPort.set_protocol("Frame:1ms");
            spiPort.set_ssPolarity(YSpiPort.SSPOLARITY_ACTIVE_LOW);
            module.saveToFlash();
            YAPI.Sleep(25, ref errmsg);
            spiPort.writeHex(SET_MODE_4);
            YAPI.Sleep(5, ref errmsg);

            string[] commands = { READ_STATUS, READ_STATUS, READ_STATUS, READ_ID, SET_ANGLES };
            Frame[] result;
            if(!SendAndReceive(commands, out result))
            {
                Console.WriteLine("Failed to initialize SCL3300 (communication error)");
                return false;
            }
            if(!_chip_ready)
            {
                Console.WriteLine("SCL3300 startup failed (rs={4})", result[2].rs);
                return false;
            }
            if((result[3].data & 0xff) != 0xc1)
            {
                Console.WriteLine("Unexpected SCL3300 identification (WHOAMI={0})", (result[3].data & 0xff));
                return false;
            }
            if(!DecodeStatus(result[2]))
            {
                Console.WriteLine("SCL3300 Status bad, chip reset is required");
                return false;
            }
            Console.WriteLine("SCL3300 is ready");
            return true;
        }

        private bool DecodeStatus(Frame status)
        {
            bool need_reboot = false;
            if ((status.data & 1) != 0) {
                Console.WriteLine("Component internal connection error");
            }

            if ((status.data & (1 << 1)) != 0) {
                Console.WriteLine("Operation mode changed");
                need_reboot = true;
            }
            if ((status.data & (1 << 2)) != 0)
            {
                Console.WriteLine("Device in power down mode");
                need_reboot = true;
            }
            if ((status.data & (1 << 3)) != 0)
            {
                Console.WriteLine("Error in non-volatile memory");
                need_reboot = true;
            }
            if ((status.data & (1 << 4)) != 0)
            {
                Console.WriteLine("Voltage level failure");
                need_reboot = true;
            }
            if ((status.data & (1 << 5)) != 0)
            {
                Console.WriteLine("Temperature signal path saturated");
            }
            if ((status.data & (1 << 6)) != 0)
            {
                Console.WriteLine("Signal saturated in signal path");
            }
            if ((status.data & (1 << 7)) != 0)
            {
                Console.WriteLine("Clock error");
                need_reboot = true;
            }
            if ((status.data & (1 << 8)) != 0)
            {
                Console.WriteLine("Digital block error type 2");
                need_reboot = true;
            }
            if ((status.data & (1 << 9)) != 0)
            {
                Console.WriteLine("Digital block error type 1");
                need_reboot = true;
            }
            return !need_reboot;
        }

        public override bool CheckChipsetID()
        {
            string[] commands = { READ_ID };
            Frame[] result;
            if (!SendAndReceive(commands, out result))
            {
                Console.WriteLine("Failed to query chipset ID (communication error)");
                return false;
            }
            if (!_chip_ready)
            {
                Console.WriteLine("SCL3300 startup failed (rs={4})", result[0].rs);
                return false;
            }
            if ((result[0].data & 0xff) != 0xc1)
            {
                Console.WriteLine("Unexpected SCL3300 identification (WHOAMI={0})", (result[0].data & 0xff));
                return false;
            }
            return true;
        }

        public override void refreshState()
        {
            string[] commands = { READ_ANGLE_X, READ_ANGLE_Y, READ_ANGLE_Z, READ_STATUS };
            Frame[] result;
            if (!SendAndReceive(commands, out result))
            {
                Console.WriteLine("Failed to read from SCL3300 (communication error)");
                return;
            }
            _angle_x = (double)result[0].data / (1 << 14) * 90.0;
            _angle_y = (double)result[1].data / (1 << 14) * 90.0;
            _angle_z = (double)result[2].data / (1 << 14) * 90.0;
            DecodeStatus(result[3]);

            //Console.WriteLine("AngleX={0:F1}° AngleY={2:1}° AngleZ={4:1}° ({1} / {3} / {5})", _angle_x, frame_x.ToString(), _angle_y, frame_y.ToString(), _angle_z, frame_z.ToString());
        }
    }
}