using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;

namespace OpenKNX.Toolbox.Sign
{
    class HardwareSigner
    {
        public HardwareSigner(
                FileInfo hardwareFile,
                IDictionary<string, string> applProgIdMappings,
                IDictionary<string, string> applProgHashes,
                string basePath,
                int nsVersion,
                bool patchIds)
        {
            Assembly asm = Assembly.LoadFrom(Path.Combine(basePath, "Knx.Ets.XmlSigning.dll"));
            Assembly objm = Assembly.LoadFrom(Path.Combine(basePath, "Knx.Ets.Xml.ObjectModel.dll"));

            Type RegistrationKeyEnum = objm.GetType("Knx.Ets.Xml.ObjectModel.RegistrationKey");
            object registrationKey = Enum.Parse(RegistrationKeyEnum, "knxconv");

            System.Version lVersion = asm.GetName().Version;
            if(lVersion >= new System.Version("6.2.0")) { //ab ETS6.2
                objm = Assembly.LoadFrom(Path.Combine(basePath, "Knx.Ets.Common.dll"));
                object knxSchemaVersion = Enum.ToObject(objm.GetType("Knx.Ets.Common.Schema.KnxXmlSchemaVersion"), nsVersion);
                _type = asm.GetType("Knx.Ets.XmlSigning.Signer.HardwareSigner");
                _instance = Activator.CreateInstance(_type, hardwareFile, applProgIdMappings, applProgHashes, patchIds, registrationKey, knxSchemaVersion);
            } else if(lVersion >= new System.Version("6.0.0")) { //ab ETS6.0/6.1
                object knxSchemaVersion = Enum.ToObject(objm.GetType("Knx.Ets.Xml.ObjectModel.KnxXmlSchemaVersion"), nsVersion);
                _type = asm.GetType("Knx.Ets.XmlSigning.Signer.HardwareSigner");
                if (lVersion < new System.Version("6.1.0"))
                    _type = asm.GetType("Knx.Ets.XmlSigning.HardwareSigner");
                _instance = Activator.CreateInstance(_type, hardwareFile, applProgIdMappings, applProgHashes, patchIds, registrationKey, knxSchemaVersion);
            } else {
                _type = asm.GetType("Knx.Ets.XmlSigning.HardwareSigner");
                _instance = Activator.CreateInstance(_type, hardwareFile, applProgIdMappings, applProgHashes, patchIds, registrationKey);
            }
        }

        public void SignFile()
        {
            _type.GetMethod("SignFile", BindingFlags.Instance | BindingFlags.Public).Invoke(_instance, null);
        }

        private readonly object _instance;
        private readonly Type _type;

        public IDictionary<string, string> OldNewIdMappings
        {
            get
            {
                return (IDictionary<string, string>)_type.GetProperty("OldNewIdMappings", BindingFlags.Public | BindingFlags.Instance).GetValue(_instance);
            }
        }
    }
}