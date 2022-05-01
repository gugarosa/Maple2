﻿using System;
using System.Text.Json.Serialization;

namespace Maple2.Model.Metadata;

public class MapEntity {
    public string XBlock { get; set; }
    public Guid Guid { get; set; }
    public string Name { get; set; }
    
    public MapBlock Block { get; set; }
    
    public MapEntity(string xBlock, Guid guid, string name) {
        XBlock = xBlock;
        Guid = guid;
        Name = name;
    }
}

public abstract partial record MapBlock([JsonDiscriminator] MapBlock.Discriminator Class) {
    public enum Discriminator : uint {
        Portal = 19716277,
        //SpawnPoint = 2593567611,
        SpawnPointPC = 476587788,
    }
}