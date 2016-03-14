﻿namespace ArrayNDNS

open Basics


/// Functions for storing ArrayNDTs in HDF5 files.
module ArrayNDHDF =

    /// Writes the given ArrayNDT into the HDF5 file under the given name.
    let write (hdf5: HDF5) name (hostAry: ArrayNDHostT<'T>) =
        let hostAry = ArrayND.makeContiguous hostAry
        
        match hostAry.Storage with
        | :? ManagedArrayStorageT<'T> as storage ->
            hdf5.Write (name, storage.Data, ArrayND.shape hostAry)
        | _ -> failwith "currently only ManagedArrayStorage is supported"

    /// Reads the ArrayNDT with the given name from an HDF5 file.
    let read<'T> (hdf5: HDF5) name =
        let (data: 'T array), shape = hdf5.Read (name)       
        ArrayNDHostT (ArrayNDLayout.newContiguous shape, ManagedArrayStorageT (data)) 
        
