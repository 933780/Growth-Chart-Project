// Data extracted from: http://www.cdc.gov/growthcharts/percentile_data_files.htm
//                      http://www.cdc.gov/growthcharts/who_charts.htm
// On 2012-11-28
// By Nikolai Schwertner, MedAppTech

/* global jQuery */

// Initialize the GC global object as needed
var GC;
if (!GC) {
    GC = {};
}

(function ($) {
    "use strict";

    /**
     * Returns the dataset object for the given source/type/gender/age range.
     *
     * src examples: "WHO", "IAP", "CDC", "IAP+WHO"
     * "IAP+WHO_LENGTH", "IAP+WHO_WEIGHT", "IAP+WHO_BMI" are real keys baked
     * directly into GCCurveDataJSON.txt — no runtime stitching needed.
     *
     * @param {String} src         WHO|IAP|CDC|IAP+WHO|...
     * @param {String} type        LENGTH|WEIGHT|HEADC|BMI
     * @param {String} gender      male|female
     * @param {Number} startAgeMos
     * @param {Number} endAgeMos
     */
    GC.getDataSet = function( src, type, gender, startAgeMos, endAgeMos ) {

        var tmp, ds, i, range, a = 0, n, out = null;

        tmp = GC.DATA_SETS[src + "_" + type];
        if ( !tmp ) {
            if (type === "LENGTH") { tmp = GC.DATA_SETS[src + "_STATURE"]; }
            if ( !tmp ) { return null; }
        }

        // Convert single dataset object to array for uniform handling
        if ( Object.prototype.toString.call( tmp ) !== "[object Array]" ) {
            tmp = [ tmp ];
        }

        // Pick the dataset that covers the most of the requested age range
        for ( i = tmp.length - 1; i >= 0; i-- ) {
            ds    = tmp[i];
            range = GC.getDataSetAgeRange( ds )[ gender ];

            if ( range.min <= endAgeMos && range.max >= startAgeMos ) {
                n = endAgeMos -
                    startAgeMos -
                    Math.max(endAgeMos - range.max, 0) -
                    Math.max(range.min - startAgeMos, 0);

                if ( n > a ) {
                    a   = n;
                    out = ds;
                }
            }
        }

        return out;
    };

    /**
     * Returns the age range covered by a dataset for each gender.
     * Result is cached on the dataset object itself.
     *
     * @param  {Object} ds  One entry from GC.DATA_SETS
     * @returns {Object}    { male: {min, max}, female: {min, max} }
     */
    GC.getDataSetAgeRange = function( ds ) {

        function sortByAge(a, b) { return a.Agemos - b.Agemos; }

        if ( !ds.ageRange ) {

            ds.ageRange = {
                "male"   : { min: null, max: null },
                "female" : { min: null, max: null }
            };

            var genders = { male: 1, female: 1 },
                currentGender, data, len, type, min, max, x, group;

            for ( currentGender in genders ) {
                data = ds.data[currentGender];
                type = Object.prototype.toString.call(data);

                if ( type === "[object Array]" ) {
                    data.sort(sortByAge);
                    len = data.length;
                    ds.ageRange[currentGender].min = data[0].Agemos;
                    ds.ageRange[currentGender].max = data[len - 1].Agemos;
                }
                else if ( type === "[object Object]" ) {
                    min = Number.MAX_VALUE;
                    max = Number.MIN_VALUE;
                    for ( x in data ) {
                        group = data[x];
                        group.sort(sortByAge);
                        len = group.length;
                        min = Math.min(min, group[0].Agemos);
                        max = Math.max(max, group[len - 1].Agemos);
                    }
                    ds.ageRange[currentGender].min = min;
                    ds.ageRange[currentGender].max = max;
                }
            }
        }

        return ds.ageRange;
    };

    /**
     * Adjusts premature dataset age entries for the patient's gestational age.
     */
    GC.translatePreemieDatasets = function(patient) {
        if (patient.weeker) {
            var diff = patient.weeker / 4.348214285714286;
            $.each(GC.DATA_SETS, function(type, ds) {
                if (ds.isPremature) {
                    $.each(["male", "female"], function(i, gender) {
                        $.each(ds.data[gender] || [], function(j, rec) {
                            rec.Agemos -= diff;
                        });
                    });
                }
            });
        }
    };

}(jQuery));