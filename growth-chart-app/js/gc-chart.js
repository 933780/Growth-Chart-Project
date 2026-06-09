// Growth Charts prototype
// Nikolai Schwertner, MedAppTech

// Initialize the GC global object as needed
window.GC = window.GC || {};

(function (GC) {
    "use strict";

    GC.generateCurveSeries = function (dataSet, gender, percentile, startAge, endAge) {
        var data   = dataSet.data[gender],
            len    = data.length,
            points = [],
            i, age, entry;

        // If the dataset uses Length as X (Weight-for-Length), entries contain
        // a `Length` field and LMS parameters (L,M,S). Generate points by
        // converting LMS -> value and using Length as x.
        if (len && data[0].Length !== undefined && data[0].L !== undefined) {
            function percentileToZ(p) {
                var c0 = 2.515517, c1 = 0.802853, c2 = 0.010328,
                    d1 = 1.432788, d2 = 0.189269, d3 = 0.001308,
                    t, num, den, z;

                if (p <= 0) { return -Infinity; }
                if (p >= 1) { return  Infinity; }

                var upper = (p > 0.5);
                var q = upper ? (1 - p) : p;

                t   = Math.sqrt(-2 * Math.log(q));
                num = c0 + t * (c1 + t * c2);
                den = 1  + t * (d1 + t * (d2 + t * d3));
                z   = t - num / den;

                return upper ? z : -z;
            }

            function lmsToValue(L, M, S, percentile) {
                var Z = percentileToZ(percentile);
                var X;
                if (Math.abs(L) < 1e-6) {
                    X = M * Math.exp(S * Z);
                } else {
                    var inner = 1 + L * S * Z;
                    if (inner <= 0) { return null; }
                    X = M * Math.pow(inner, 1 / L);
                }
                return (isFinite(X) && X > 0) ? X : null;
            }

            for (i = 0; i < len; i++) {
                entry = data[i];
                var weightVal = lmsToValue(entry.L, entry.M, entry.S, percentile);
                if (weightVal === null) { continue; }

                age = entry.Length;
                points.push({ x: age, y: weightVal });
            }

            return points;
        }

        // Default: age (Agemos) keyed curves
        for (i = 0; i < len; i++) {
            age = data[i].Agemos;

            // Limit in time if needed
            if ( !(!dataSet.isPremature && (
                 ((startAge || startAge === 0) && age < startAge) ||
                 ((endAge   || endAge   === 0) && age > endAge)) )) {
                points.push({
                    x: age,
                    y: GC.findXFromPercentile(percentile, dataSet, gender, age)
                });
            }
        }

        return points;
    };

    GC.convertPointsSet = function ( dataPoints, startAge, endAge ) {
        var data = dataPoints,
            points = [],
            i, age;

        for (i = 0; i < data.length; i++) {
            age = data[i].Agemos;

            // Limit in time if needed
            if ( !((startAge && age < startAge) || (endAge && age > endAge))) {
                points.push({
                    x: age,
                    y: data[i].value
                });
            }
        }

        return points.sort(function(a,b) {
            return a.x - b.x;
        });
    };

}(GC));
